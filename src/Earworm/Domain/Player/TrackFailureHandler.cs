using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.Entities;
using Earworm.Domain.Queue;
using Earworm.Persistence.Repositories;

namespace Earworm.Domain.Player;

/// <summary>
/// Subscribes to PlayerEngine.TrackFailed and posts a human-readable error
/// notice to the configured logging channel, per PRD §11:
///
///   "When a stream fails mid-track or yt-dlp can't resolve a queued URL, the
///    bot auto-skips to the next track and posts a failure notice to the
///    designated logging channel ... e.g. 'Couldn't play "Numb"
///    (https://...) — yt-dlp: HTTP 410 (video removed)'."
///
/// Before this class existed, TrackFailed had no subscriber, so the operator
/// surface was entirely silent on failures — the user just saw a track they
/// queued mysteriously not play.
/// </summary>
public sealed class TrackFailureHandler : IDisposable
{
    private readonly PlayerEngine _player;
    private readonly DiscordClient _discord;
    private readonly ISettingsRepository _settings;
    private readonly ILogger<TrackFailureHandler> _logger;

    public TrackFailureHandler(
        PlayerEngine player,
        DiscordClient discord,
        ISettingsRepository settings,
        ILogger<TrackFailureHandler> logger)
    {
        _player = player;
        _discord = discord;
        _settings = settings;
        _logger = logger;

        _player.TrackFailed += OnTrackFailed;
    }

    private void OnTrackFailed(QueueItem track, string failureReason)
    {
        _ = Task.Run(() => PostFailureNoticeAsync(track, failureReason));
    }

    private async Task PostFailureNoticeAsync(QueueItem track, string failureReason)
    {
        try
        {
            var channelId = await _settings.GetLoggingChannelIdAsync();
            if (!channelId.HasValue)
            {
                _logger.LogInformation("Track failed but no logging channel configured; skipping notice. Title='{Title}' reason='{Reason}'", track.Title, failureReason);
                return;
            }

            var channel = await _discord.GetChannelAsync(channelId.Value);
            if (channel == null)
            {
                _logger.LogWarning("Logging channel {ChannelId} not resolvable; dropping failure notice.", channelId.Value);
                return;
            }

            string sourceLink = track.SourceType.Equals("youtube", StringComparison.OrdinalIgnoreCase)
                ? $"https://www.youtube.com/watch?v={track.SourceId}"
                : track.SourceType.Equals("soundcloud", StringComparison.OrdinalIgnoreCase)
                    ? "SoundCloud track"
                    : "uploaded MP3";

            string title = string.IsNullOrEmpty(track.Title) ? "(unknown title)" : track.Title;
            string requester = string.IsNullOrEmpty(track.RequestedByUserId)
                ? track.RequestedByDisplayName ?? "(unknown)"
                : $"<@{track.RequestedByUserId}>";

            // Short, human-readable, one line — matches PRD §11's example shape.
            var message = $"⚠️ Couldn't play **{title}** ({sourceLink}) — {failureReason}. Requester: {requester}.";

            var builder = new DiscordMessageBuilder()
                .WithContent(message)
                .WithAllowedMentions(Mentions.None);

            await channel.SendMessageAsync(builder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post track-failure notice to logging channel.");
        }
    }

    public void Dispose()
    {
        _player.TrackFailed -= OnTrackFailed;
    }
}
