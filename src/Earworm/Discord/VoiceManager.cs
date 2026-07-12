using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
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

    private readonly ConcurrentDictionary<ulong, TimerRegistration> _emptyChannelTimers = new();
    private readonly ConcurrentDictionary<ulong, TimerRegistration> _idleTimers = new();
    private readonly ConcurrentDictionary<ulong, byte> _blockedTimerGuilds = new();
    private readonly object _joinTimerBlocksLock = new();
    private readonly Dictionary<ulong, int> _joinTimerBlocks = new();

    private sealed class TimerRegistration
    {
        private const int Pending = 0;
        private const int Expiring = 1;
        private const int Cancelled = 2;

        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;
        private int _completed;

        public TimerRegistration(CancellationToken shutdownToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        }

        public CancellationToken Token => _cancellation.Token;
        public Task Completion => _completion.Task;
        public bool IsCancelled => Volatile.Read(ref _state) == Cancelled;

        public bool TryClaimExpiry() =>
            Interlocked.CompareExchange(ref _state, Expiring, Pending) == Pending;

        /// <summary>
        /// Cancels a timer that has not started its expiry action. If expiry
        /// already won the race, the caller must await <see cref="Completion"/>
        /// before allowing a replacement guild generation to start.
        /// </summary>
        public bool Cancel()
        {
            int previous = Interlocked.CompareExchange(ref _state, Cancelled, Pending);
            if (previous == Pending || previous == Cancelled)
            {
                try { _cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                return true;
            }

            return false;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _completion.TrySetResult();
            _cancellation.Dispose();
        }
    }

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
            engine.PlaybackBecameIdle += OnPlaybackBecameIdle;
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

        var guildId = channel.Guild.Id.ToString();
        // Resolve the tenant-owned engine before creating a Lavalink player so
        // a guild already blocked by suspension cannot reconnect to voice.
        var engine = _playerEngines.GetOrCreate(guildId);

        // Prevent timer publication and expiry across the whole replacement
        // window. A timer that already claimed expiry is drained before the
        // Lavalink player is retrieved, so it cannot disconnect the new player.
        // This is a separate, reference-counted block from tenant suspension;
        // releasing it can never accidentally re-enable a suspended tenant.
        BeginJoinTimerBlock(channel.Guild.Id);
        try
        {
            await DrainGuildTimersForJoinAsync(channel.Guild.Id);

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

            // Suspension can race a join that passed the whitelist check just
            // before access was revoked. Re-check after the network await and tear
            // down the newly created player instead of leaving a suspended guild
            // connected after lifecycle cleanup has already run.
            if (_playerEngines.IsBlocked(guildId))
            {
                var blockedPlayer = await _audioService.Players.GetPlayerAsync<LavalinkPlayer>(channel.Guild.Id);
                if (blockedPlayer != null) await blockedPlayer.DisconnectAsync();
                throw new GuildAccessBlockedException(guildId);
            }

            // A suspend followed by a quick re-admit can replace the engine while
            // RetrieveAsync is pending. Always continue through the registry's
            // current generation rather than a canceled/evicted instance captured
            // before the await.
            try
            {
                engine = _playerEngines.GetOrCreate(guildId);
            }
            catch (GuildAccessBlockedException)
            {
                var blockedPlayer = await _audioService.Players.GetPlayerAsync<LavalinkPlayer>(channel.Guild.Id);
                if (blockedPlayer != null) await blockedPlayer.DisconnectAsync();
                throw;
            }

            // Catch a timer that was published just before the temporary block
            // became visible. No replacement can appear while this drain runs.
            await DrainGuildTimersForJoinAsync(channel.Guild.Id);
        }
        finally
        {
            EndJoinTimerBlock(channel.Guild.Id);
        }

        _logger.LogInformation("Lavalink player ready for guild {GuildId}.", channel.Guild.Id);

        // Reset idle/empty-channel timers and kick off playback if queue is non-empty.
        CancelIdleTimer(channel.Guild.Id);
        CheckChannelEmptyState(channel);

        await engine.MaybeStartAsync();
    }

    public async Task LeaveChannelAsync(ulong guildId)
    {
        _logger.LogInformation("Leaving voice channel in guild {GuildId}.", guildId);

        CancelEmptyChannelTimer(guildId);
        CancelIdleTimer(guildId);

        Exception? stopFailure = null;

        // Only stop an engine that actually exists — don't construct one just to
        // leave a channel. A stop failure must not prevent the independent voice
        // disconnect attempt below.
        if (_playerEngines.TryGet(guildId.ToString(), out var engine))
        {
            try
            {
                await engine.StopAsync();
            }
            catch (Exception ex)
            {
                stopFailure = ex;
                _logger.LogWarning(
                    ex,
                    "Failed to stop playback while leaving guild {GuildId}; continuing with voice disconnect.",
                    guildId);
            }
        }

        try
        {
            var player = await _audioService.Players.GetPlayerAsync<LavalinkPlayer>(guildId);
            if (player != null)
            {
                await player.DisconnectAsync();
            }
        }
        catch (Exception disconnectFailure)
        {
            _logger.LogWarning(
                disconnectFailure,
                "Failed to disconnect voice while leaving guild {GuildId}.",
                guildId);
            if (stopFailure is not null)
            {
                throw new AggregateException(
                    "Playback stop and voice disconnect both failed.",
                    stopFailure,
                    disconnectFailure);
            }

            ExceptionDispatchInfo.Capture(disconnectFailure).Throw();
        }

        if (stopFailure is not null)
        {
            ExceptionDispatchInfo.Capture(stopFailure).Throw();
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
            StartTimer(
                _emptyChannelTimers,
                guildId,
                TimeSpan.FromSeconds(graceSeconds),
                "empty-channel",
                async () =>
                {
                    _logger.LogInformation("Empty-channel grace expired; auto-disconnecting.");
                    await LeaveChannelAsync(guildId);
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

    private void OnPlaybackBecameIdle(string guildId)
    {
        if (ulong.TryParse(guildId, out ulong numericGuildId))
        {
            StartIdleTimer(numericGuildId);
        }
    }

    private void StartIdleTimer(ulong guildId)
    {
        int idleSeconds = _config.AutoBehavior.IdleDisconnectSeconds;
        StartTimer(
            _idleTimers,
            guildId,
            TimeSpan.FromSeconds(idleSeconds),
            "idle",
            async () =>
            {
                _logger.LogInformation("Idle timer expired; auto-disconnecting.");
                await LeaveChannelAsync(guildId);
            });
    }

    private void StartTimer(
        ConcurrentDictionary<ulong, TimerRegistration> timers,
        ulong guildId,
        TimeSpan delay,
        string timerKind,
        Func<Task> onExpired)
    {
        if (IsTimerBlocked(guildId)) return;

        var registration = new TimerRegistration(_shutdown.Token);
        while (true)
        {
            if (timers.TryAdd(guildId, registration)) break;

            if (!timers.TryGetValue(guildId, out var existing)) continue;
            if (existing.IsCancelled)
            {
                TryRemoveTimer(timers, guildId, existing);
                continue;
            }

            registration.Cancel();
            registration.Complete();
            return;
        }

        // Suspension may have begun after the optimistic check above. Do not
        // publish a worker into a blocked guild generation.
        if (IsTimerBlocked(guildId))
        {
            TryRemoveTimer(timers, guildId, registration);
            registration.Cancel();
            registration.Complete();
            return;
        }

        _logger.LogInformation(
            "Starting {TimerKind} disconnect timer of {Seconds}s for guild {GuildId}.",
            timerKind,
            delay.TotalSeconds,
            guildId);

        try
        {
            _ = Task.Run(
                () => RunTimerAsync(timers, guildId, registration, timerKind, delay, onExpired),
                CancellationToken.None);
        }
        catch
        {
            TryRemoveTimer(timers, guildId, registration);
            registration.Cancel();
            registration.Complete();
            throw;
        }
    }

    private async Task RunTimerAsync(
        ConcurrentDictionary<ulong, TimerRegistration> timers,
        ulong guildId,
        TimerRegistration registration,
        string timerKind,
        TimeSpan delay,
        Func<Task> onExpired)
    {
        try
        {
            await Task.Delay(delay, registration.Token);
            if (_shutdown.IsShuttingDown || IsTimerBlocked(guildId)) return;
            if (!registration.TryClaimExpiry()) return;

            await onExpired();
        }
        catch (OperationCanceledException) when (registration.Token.IsCancellationRequested)
        {
            _logger.LogInformation("{TimerKind} timer cancelled for guild {GuildId}.", timerKind, guildId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{TimerKind} timer failed for guild {GuildId}.", timerKind, guildId);
        }
        finally
        {
            TryRemoveTimer(timers, guildId, registration);
            registration.Complete();
        }
    }

    private static bool TryRemoveTimer(
        ConcurrentDictionary<ulong, TimerRegistration> timers,
        ulong guildId,
        TimerRegistration expected)
    {
        // Conditional key+value removal prevents an old timer's finally block
        // from deleting a replacement installed for the same guild (ABA race).
        return ((ICollection<KeyValuePair<ulong, TimerRegistration>>)timers)
            .Remove(new KeyValuePair<ulong, TimerRegistration>(guildId, expected));
    }

    private void CancelEmptyChannelTimer(ulong guildId)
    {
        if (_emptyChannelTimers.TryGetValue(guildId, out var timer)) timer.Cancel();
    }

    private void CancelIdleTimer(ulong guildId)
    {
        if (_idleTimers.TryGetValue(guildId, out var timer)) timer.Cancel();
    }

    /// <summary>
    /// Prevents new timers for a suspended guild, cancels pending timers, and
    /// waits for any expiry action that already won the race. The tenant
    /// lifecycle gate keeps re-admission serialized until this drain completes.
    /// </summary>
    public async Task CancelGuildTimersAndDrainAsync(ulong guildId)
    {
        _blockedTimerGuilds[guildId] = 0;

        await DrainGuildTimersAsync(guildId);
    }

    /// <summary>
    /// Drains timer workers that could target a voice session being replaced by
    /// an explicit join, without preventing future idle/empty timers.
    /// </summary>
    public Task DrainGuildTimersForJoinAsync(ulong guildId) => DrainGuildTimersAsync(guildId);

    private async Task DrainGuildTimersAsync(ulong guildId)
    {
        while (true)
        {
            var completions = new List<Task>(2);
            if (_emptyChannelTimers.TryGetValue(guildId, out var emptyTimer))
            {
                emptyTimer.Cancel();
                completions.Add(emptyTimer.Completion);
            }
            if (_idleTimers.TryGetValue(guildId, out var idleTimer))
            {
                idleTimer.Cancel();
                completions.Add(idleTimer.Completion);
            }

            if (completions.Count == 0) return;
            await Task.WhenAll(completions);
        }
    }

    private void BeginJoinTimerBlock(ulong guildId)
    {
        lock (_joinTimerBlocksLock)
        {
            _joinTimerBlocks.TryGetValue(guildId, out int count);
            _joinTimerBlocks[guildId] = checked(count + 1);
        }
    }

    private void EndJoinTimerBlock(ulong guildId)
    {
        lock (_joinTimerBlocksLock)
        {
            if (!_joinTimerBlocks.TryGetValue(guildId, out int count)) return;
            if (count <= 1) _joinTimerBlocks.Remove(guildId);
            else _joinTimerBlocks[guildId] = count - 1;
        }
    }

    private bool IsTimerBlocked(ulong guildId)
    {
        if (_blockedTimerGuilds.ContainsKey(guildId)) return true;

        lock (_joinTimerBlocksLock)
        {
            return _joinTimerBlocks.ContainsKey(guildId);
        }
    }

    /// <summary>Allows timers again after an explicit tenant admission.</summary>
    public void AllowGuildTimers(ulong guildId) => _blockedTimerGuilds.TryRemove(guildId, out _);

    /// <summary>Blocks timer creation for a tenant already suspended at startup.</summary>
    public void BlockGuildTimers(ulong guildId) => _blockedTimerGuilds[guildId] = 0;

    public void Dispose()
    {
        foreach (var engine in _playerEngines.CreatedInstances())
        {
            engine.TrackStarted -= OnTrackStarted;
            engine.PlaybackBecameIdle -= OnPlaybackBecameIdle;
        }
        _discordClient.VoiceStateUpdated -= OnVoiceStateUpdatedAsync;

        foreach (var t in _emptyChannelTimers.Values) t.Cancel();
        foreach (var t in _idleTimers.Values) t.Cancel();
    }
}
