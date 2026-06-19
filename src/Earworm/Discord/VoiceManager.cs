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
using Earworm.Infrastructure;

namespace Earworm.Discord;

public sealed class VoiceManager : IDisposable
{
    private readonly DiscordClient _discordClient;
    private readonly IAudioService _audioService;
    private readonly PerGuildRegistry<PlayerEngine> _playerEngines;
    private readonly PerGuildRegistry<QueueManager> _queueManagers;
    private readonly EarwormConfig _config;
    private readonly ILogger<VoiceManager> _logger;
    private readonly ShutdownLifetime _shutdown;

    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _emptyChannelTimers = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _idleTimers = new();

    public VoiceManager(
        DiscordClient discordClient,
        IAudioService audioService,
        PerGuildRegistry<PlayerEngine> playerEngines,
        PerGuildRegistry<QueueManager> queueManagers,
        EarwormConfig config,
        ILogger<VoiceManager> logger,
        ShutdownLifetime? shutdown = null)
    {
        _discordClient = discordClient;
        _audioService = audioService;
        _playerEngines = playerEngines;
        _queueManagers = queueManagers;
        _config = config;
        _logger = logger;
        // ShutdownLifetime is a process-wide singleton in production DI, but the
        // tests construct VoiceManager directly without it. Fall back to a local
        // instance so the fire-and-forget paths always have a token to observe.
        _shutdown = shutdown ?? new ShutdownLifetime();

        // Attach idle/empty-channel bookkeeping to every per-guild engine —
        // those already created and any created later.
        _playerEngines.AddInitializer(engine =>
        {
            engine.TrackStarted += OnTrackStarted;
            engine.TrackEnded += OnTrackEnded;
        });
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

        await _playerEngines.GetOrCreate(channel.Guild.Id.ToString()).MaybeStartAsync();
    }

    public async Task LeaveChannelAsync(ulong guildId)
    {
        _logger.LogInformation("Leaving voice channel in guild {GuildId}.", guildId);

        CancelEmptyChannelTimer(guildId);
        CancelIdleTimer(guildId);

        // Only stop an engine that actually exists — don't construct one just to
        // leave a channel.
        if (_playerEngines.TryGet(guildId.ToString(), out var engine))
        {
            await engine.StopAsync();
        }

        var player = await _audioService.Players.GetPlayerAsync<LavalinkPlayer>(guildId);
        if (player != null)
        {
            await player.DisconnectAsync();
        }
    }

    private Task OnVoiceStateUpdatedAsync(DiscordClient sender, VoiceStateUpdateEventArgs e)
    {
        var ct = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

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

                    // Only act if this guild already has a live engine. Don't
                    // construct one (possibly for a non-tenant guild the bot
                    // happens to sit in) just because the bot's mute flag flipped —
                    // a constructed engine subscribes to the shared audio service
                    // and would never be reclaimed.
                    if (_playerEngines.TryGet(guild.Id.ToString(), out var engine))
                    {
                        if (isMuted && !wasMuted)
                        {
                            _logger.LogInformation("Bot server-muted; pausing.");
                            await engine.PauseAsync();
                        }
                        else if (!isMuted && wasMuted)
                        {
                            _logger.LogInformation("Bot server-unmuted; resuming.");
                            await engine.ResumeAsync();
                        }
                    }
                    return;
                }

                // Empty-channel detection.
                var botChannel = bot.VoiceState?.Channel;
                if (botChannel != null) CheckChannelEmptyState(botChannel);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: swallow.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling VoiceStateUpdated event.");
            }
        }, ct);
        return Task.CompletedTask;
    }

    private void CheckChannelEmptyState(DiscordChannel botChannel)
    {
        ulong guildId = botChannel.Guild.Id;
        bool hasNonBot = botChannel.Users.Any(u => !u.IsBot);

        if (!hasNonBot)
        {
            int graceSeconds = _config.AutoBehavior.EmptyChannelGraceSeconds;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
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
                    if (_shutdown.IsShuttingDown) return;
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
            }, _shutdown.Token);
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
        var ct = _shutdown.Token;
        _ = Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (ulong.TryParse(track.GuildId, out ulong guildId))
                {
                    if (_queueManagers.GetOrCreate(track.GuildId).Count == 0) StartIdleTimer(guildId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: swallow.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking queue state on TrackEnded.");
            }
        }, ct);
    }

    private void StartIdleTimer(ulong guildId)
    {
        int idleSeconds = _config.AutoBehavior.IdleDisconnectSeconds;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
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
                if (_shutdown.IsShuttingDown) return;
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
        }, _shutdown.Token);
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
        foreach (var engine in _playerEngines.CreatedInstances())
        {
            engine.TrackStarted -= OnTrackStarted;
            engine.TrackEnded -= OnTrackEnded;
        }
        _discordClient.VoiceStateUpdated -= OnVoiceStateUpdatedAsync;

        foreach (var t in _emptyChannelTimers.Values) { t.Cancel(); t.Dispose(); }
        foreach (var t in _idleTimers.Values) { t.Cancel(); t.Dispose(); }
    }
}
