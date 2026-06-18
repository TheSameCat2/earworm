using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.Entities;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Infrastructure;
using Earworm.Persistence.Repositories;

namespace Earworm.Discord;

public sealed class NowPlayingPoster : IDisposable
{
    private readonly DiscordClient _discordClient;
    private readonly PerGuildRegistry<PlayerEngine> _playerEngines;
    private readonly ISettingsRepository _settings;
    private readonly ILogger<NowPlayingPoster> _logger;
    private readonly ShutdownLifetime _shutdown;

    public NowPlayingPoster(
        DiscordClient discordClient,
        PerGuildRegistry<PlayerEngine> playerEngines,
        ISettingsRepository settings,
        ILogger<NowPlayingPoster> logger,
        ShutdownLifetime shutdown)
    {
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _playerEngines = playerEngines ?? throw new ArgumentNullException(nameof(playerEngines));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));

        _playerEngines.AddInitializer(engine => engine.TrackStarted += OnTrackStarted);
    }

    private void OnTrackStarted(QueueItem track)
    {
        var ct = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var channelId = await _settings.GetNowPlayingChannelIdAsync(track.GuildId);
                if (channelId is not ulong nowPlayingChannelId) return;

                var channel = await _discordClient.GetChannelAsync(nowPlayingChannelId);
                if (channel == null)
                {
                    _logger.LogWarning("Could not find now playing channel with ID: {ChannelId}", nowPlayingChannelId);
                    return;
                }

                var embed = new DiscordEmbedBuilder()
                    .WithTitle("Now Playing 🎶")
                    .WithColor(new DiscordColor("#1DB954"))
                    .WithTimestamp(DateTimeOffset.UtcNow);

                if (!string.IsNullOrEmpty(track.Title)) embed.AddField("Track", track.Title, inline: true);
                if (!string.IsNullOrEmpty(track.Artist)) embed.AddField("Artist", track.Artist, inline: true);

                string durationStr = "Unknown";
                if (track.DurationSeconds.HasValue)
                {
                    var ts = TimeSpan.FromSeconds(track.DurationSeconds.Value);
                    durationStr = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
                }
                embed.AddField("Duration", durationStr, inline: true);

                string requesterStr = string.IsNullOrEmpty(track.RequestedByUserId)
                    ? track.RequestedByDisplayName
                    : $"<@{track.RequestedByUserId}>";
                embed.AddField("Requested By", requesterStr, inline: true);

                if (track.SourceType.Equals("youtube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(track.SourceId))
                {
                    embed.WithThumbnail($"https://img.youtube.com/vi/{track.SourceId}/hqdefault.jpg");
                }
                else if (track.SourceType.Equals("mp3_upload", StringComparison.OrdinalIgnoreCase))
                {
                    embed.WithFooter("Direct File Upload");
                }

                // PRD §8: normal channel post (not a reply), suppress notifications, no mentions.
                var msg = new DiscordMessageBuilder()
                    .AddEmbed(embed)
                    .WithAllowedMentions(Mentions.None);
                msg.SuppressNotifications();

                await channel.SendMessageAsync(msg);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: swallow.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to post now playing embed for track: {Title}", track.Title);
            }
        }, ct);
    }

    public void Dispose()
    {
        foreach (var engine in _playerEngines.CreatedInstances())
        {
            engine.TrackStarted -= OnTrackStarted;
        }
    }
}
