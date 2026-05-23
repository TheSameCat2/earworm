using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Lavalink4NET;
using Lavalink4NET.Clients;
using Lavalink4NET.Players;
using Earworm.Config;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;

namespace Earworm.Discord;

public sealed class VoiceManager : IDisposable
{
    private readonly DiscordClient _discordClient;
    private readonly IAudioService _audioService;
    private readonly PlayerEngine _playerEngine;
    private readonly QueueManager _queueManager;
    private readonly EarwormConfig _config;
    private readonly ILogger<VoiceManager> _logger;

    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _emptyChannelTimers = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _idleTimers = new();

    public VoiceManager(
        DiscordClient discordClient,
        IAudioService audioService,
        PlayerEngine playerEngine,
        QueueManager queueManager,
        EarwormConfig config,
        ILogger<VoiceManager> logger)
    {
        _discordClient = discordClient;
        _audioService = audioService;
        _playerEngine = playerEngine;
        _queueManager = queueManager;
        _config = config;
        _logger = logger;

        _playerEngine.TrackStarted += OnTrackStarted;
        _playerEngine.TrackEnded += OnTrackEnded;
        _discordClient.VoiceStateUpdated += OnVoiceStateUpdatedAsync;
    }

    /// <summary>
    /// Joins the specified voice channel via Lavalink. Lavalink owns the voice
    /// gateway; the bot's only job is to send the voice-state-update payload
    /// (done implicitly by Lavalink4NET's IDiscordClientWrapper) and create a
    /// player for the guild.
    /// </summary>
    public async Task JoinChannelAsync(DiscordChannel channel)
    {
        if (channel == null) throw new ArgumentNullException(nameof(channel));

        _logger.LogInformation("Joining voice channel {ChannelName} ({ChannelId}) in guild {GuildId} via Lavalink.",
            channel.Name, channel.Id, channel.Guild.Id);

        var retrieveOptions = new PlayerRetrieveOptions(ChannelBehavior: PlayerChannelBehavior.Join);
        var playerOptions = new LavalinkPlayerOptions();

        var result = await _audioService.Players.RetrieveAsync<LavalinkPlayer, LavalinkPlayerOptions>(
            channel.Guild.Id,
            channel.Id,
            playerFactory: PlayerFactory.Default,
            options: Options.Create(playerOptions),
            retrieveOptions: retrieveOptions);

        if (!result.IsSuccess)
        {
            var msg = result.Status switch
            {
                PlayerRetrieveStatus.UserNotInVoiceChannel => "User isn't in a voice channel.",
                PlayerRetrieveStatus.BotNotConnected => "Bot isn't connected.",
                PlayerRetrieveStatus.VoiceChannelMismatch => "Bot is in a different voice channel.",
                _ => $"Could not retrieve player: {result.Status}"
            };
            throw new InvalidOperationException(msg);
        }

        _logger.LogInformation("Lavalink player ready for guild {GuildId}.", channel.Guild.Id);

        // Reset idle/empty-channel timers and kick off playback if queue is non-empty.
        CancelIdleTimer(channel.Guild.Id);
        CheckChannelEmptyState(channel);

        await _playerEngine.MaybeStartAsync();
    }

    public async Task LeaveChannelAsync(ulong guildId)
    {
        _logger.LogInformation("Leaving voice channel in guild {GuildId}.", guildId);

        CancelEmptyChannelTimer(guildId);
        CancelIdleTimer(guildId);

        await _playerEngine.StopAsync();

        var player = await _audioService.Players.GetPlayerAsync<LavalinkPlayer>(guildId);
        if (player != null)
        {
            await player.DisconnectAsync();
        }
    }

    private Task OnVoiceStateUpdatedAsync(DiscordClient sender, VoiceStateUpdateEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var guild = e.Guild;
                if (guild == null) return;
                var bot = guild.CurrentMember;
                if (bot == null) return;

                // Bot's own server-mute → pause; unmute → resume. e.Before is
                // null on a fresh voice connect (no prior state), so we need
                // to null-check both sides instead of dereferencing blind.
                if (e.User?.Id == bot.Id)
                {
                    bool wasMuted = e.Before?.IsServerMuted ?? false;
                    bool isMuted = e.After?.IsServerMuted ?? false;

                    if (isMuted && !wasMuted)
                    {
                        _logger.LogInformation("Bot server-muted; pausing.");
                        await _playerEngine.PauseAsync();
                    }
                    else if (!isMuted && wasMuted)
                    {
                        _logger.LogInformation("Bot server-unmuted; resuming.");
                        await _playerEngine.ResumeAsync();
                    }
                    return;
                }

                // Empty-channel detection.
                var botChannel = bot.VoiceState?.Channel;
                if (botChannel != null) CheckChannelEmptyState(botChannel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling VoiceStateUpdated event.");
            }
        });
        return Task.CompletedTask;
    }

    private void CheckChannelEmptyState(DiscordChannel botChannel)
    {
        ulong guildId = botChannel.Guild.Id;
        bool hasNonBot = botChannel.Users.Any(u => !u.IsBot);

        if (!hasNonBot)
        {
            int graceSeconds = _config.AutoBehavior.EmptyChannelGraceSeconds;

            var cts = new CancellationTokenSource();
            var stored = _emptyChannelTimers.GetOrAdd(guildId, cts);
            if (!ReferenceEquals(stored, cts))
            {
                cts.Dispose();
                return;
            }

            _logger.LogInformation("Voice channel empty; starting grace timer of {Seconds}s.", graceSeconds);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(graceSeconds), cts.Token);
                    _logger.LogInformation("Empty-channel grace expired; auto-disconnecting.");
                    await LeaveChannelAsync(guildId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Empty-channel timer cancelled for guild {GuildId}.", guildId);
                }
                finally
                {
                    _emptyChannelTimers.TryRemove(guildId, out _);
                    cts.Dispose();
                }
            });
        }
        else
        {
            CancelEmptyChannelTimer(guildId);
        }
    }

    private void OnTrackStarted(QueueItem track)
    {
        if (ulong.TryParse(track.GuildId, out ulong guildId))
        {
            CancelIdleTimer(guildId);
        }
    }

    private void OnTrackEnded(QueueItem track, bool skipped, string? failureReason)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (ulong.TryParse(track.GuildId, out ulong guildId))
                {
                    if (_queueManager.Count == 0) StartIdleTimer(guildId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking queue state on TrackEnded.");
            }
        });
    }

    private void StartIdleTimer(ulong guildId)
    {
        int idleSeconds = _config.AutoBehavior.IdleDisconnectSeconds;

        var cts = new CancellationTokenSource();
        var stored = _idleTimers.GetOrAdd(guildId, cts);
        if (!ReferenceEquals(stored, cts))
        {
            cts.Dispose();
            return;
        }

        _logger.LogInformation("Queue empty; starting idle disconnect timer of {Seconds}s.", idleSeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(idleSeconds), cts.Token);
                _logger.LogInformation("Idle timer expired; auto-disconnecting.");
                await LeaveChannelAsync(guildId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Idle timer cancelled for guild {GuildId}.", guildId);
            }
            finally
            {
                _idleTimers.TryRemove(guildId, out _);
                cts.Dispose();
            }
        });
    }

    private void CancelEmptyChannelTimer(ulong guildId)
    {
        if (_emptyChannelTimers.TryRemove(guildId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void CancelIdleTimer(ulong guildId)
    {
        if (_idleTimers.TryRemove(guildId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        _playerEngine.TrackStarted -= OnTrackStarted;
        _playerEngine.TrackEnded -= OnTrackEnded;
        _discordClient.VoiceStateUpdated -= OnVoiceStateUpdatedAsync;

        foreach (var t in _emptyChannelTimers.Values) { t.Cancel(); t.Dispose(); }
        foreach (var t in _idleTimers.Values) { t.Cancel(); t.Dispose(); }
    }
}
