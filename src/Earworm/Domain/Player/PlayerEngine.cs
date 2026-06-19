using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lavalink4NET;
using Lavalink4NET.Events.Players;
using Lavalink4NET.Players;
using Lavalink4NET.Protocol.Payloads.Events;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Earworm.Config;
using Earworm.Domain.DJ;
using Earworm.Domain.Queue;
using Earworm.Persistence.Repositories;

namespace Earworm.Domain.Player;

/// <summary>
/// PlayerEngine wraps Lavalink4NET's player. The bot does NOT touch PCM — it
/// sends a "play this URL" command to Lavalink over WebSocket and Lavalink
/// handles the voice transmission. We keep our QueueManager as the single
/// source of truth for the upcoming-track list and drive Lavalink one track
/// at a time (rather than letting Lavalink's QueuedLavalinkPlayer maintain
/// a parallel in-memory queue that doesn't survive restart).
/// </summary>
public class PlayerEngine : IDisposable
{
    private readonly IAudioService _audioService;
    private readonly QueueManager _queueManager;
    private readonly IQueueRepository _queueRepository;
    private readonly IHistoryRepository _historyRepository;
    private readonly IMetricsRepository _metricsRepository;
    private readonly AudioTransitionController _transitions;
    private readonly EarwormConfig _config;
    private readonly ILogger<PlayerEngine> _logger;
    private readonly ShutdownLifetime _shutdown;

    private readonly ulong _guildId;
    private readonly string _guildIdStr;
    private readonly object _stateLock = new();

    private QueueItem? _currentTrack;
    private DateTimeOffset _trackStartedAt;
    private bool _isPaused;
    private PlaybackState _cachedState;
    private ulong? _voiceChannelId;

    private const int MaxConsecutiveLoadFailures = 10;

    // When we're playing a TTS commentary track ahead of music, this TCS is
    // non-null and gets signalled by the global TrackEnded handler so the
    // music track can follow immediately. The handler also consults
    // _currentTtsIdentifier (below) so a *late* TTS TrackEnded event arriving
    // after the 30s timeout fired (which clears this TCS) is still recognized
    // as a TTS event and ignored, rather than treated as the music track
    // ending and advancing the queue a second time.
    private TaskCompletionSource? _ttsCompletion;

    // Identifier of the TTS track currently staged/playing, set when we issue
    // PlayAsync for a preroll and cleared once the matching TrackEnded event
    // is observed OR when we start the music track. Survives the TTS timeout
    // so the handler can still identify and discard a late TTS event.
    private string? _currentTtsIdentifier;

    // Set by StopAsync() (the /stop-worm and leave/disconnect path) to suppress
    // the auto-advance that the TrackEnded handler would otherwise perform when
    // it sees Reason == Stopped. The handler resets this flag after consuming
    // the next TrackEnded event, so a subsequent SkipAsync() (which calls
    // player.StopAsync() directly) still advances normally. Without this, the
    // fire-and-forget TrackEnded handler races with leave/disconnect and either
    // drops the next track from the queue or briefly starts it in a channel the
    // bot has already left.
    private bool _haltAfterTrackEnded;

    public virtual event Action<QueueItem>? TrackStarted;
    public virtual event Action<QueueItem, bool, string?>? TrackEnded; // (track, skipped, failureReason)
    public virtual event Action<QueueItem, string>? TrackFailed;
    public virtual event Action? PlaybackPaused;
    public virtual event Action? PlaybackResumed;

    /// <summary>
    /// Async hook invoked just before each track starts playing. May return a
    /// <see cref="TtsPreroll"/> — if so, PlayerEngine plays the TTS URL first
    /// (pause-insert-resume semantics), then plays the actual track, then
    /// awaits the cleanup callback so DJEngine can delete the staged file.
    /// Set via SetPreTrackHook to avoid a circular DI dependency between
    /// PlayerEngine and DJEngine.
    /// </summary>
    private Func<QueueItem, CancellationToken, Task<TtsPreroll?>>? _preTrackHook;

    public void SetPreTrackHook(Func<QueueItem, CancellationToken, Task<TtsPreroll?>> hook)
    {
        _preTrackHook = hook ?? throw new ArgumentNullException(nameof(hook));
    }

    public PlayerEngine(
        IAudioService audioService,
        QueueManager queueManager,
        IQueueRepository queueRepository,
        IHistoryRepository historyRepository,
        IMetricsRepository metricsRepository,
        AudioTransitionController transitions,
        EarwormConfig config,
        ILogger<PlayerEngine> logger,
        ShutdownLifetime shutdown,
        string guildId)
    {
        _audioService = audioService;
        _queueManager = queueManager;
        _queueRepository = queueRepository;
        _historyRepository = historyRepository;
        _metricsRepository = metricsRepository;
        _transitions = transitions;
        _config = config;
        _logger = logger;
        _shutdown = shutdown;

        _guildIdStr = guildId;
        if (!ulong.TryParse(guildId, out _guildId))
        {
            throw new InvalidOperationException($"PlayerEngine requires a valid numeric guild id; got '{guildId}'.");
        }

        _cachedState = BuildIdleState();

        _queueManager.TrackQueued += OnTrackQueued;
        _audioService.TrackEnded += OnLavalinkTrackEndedAsync;
    }

    private void OnTrackQueued(QueueItem item)
    {
        var ct = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var player = await TryGetPlayerAsync();
                if (player == null)
                {
                    _logger.LogInformation("Track queued but no active voice player; waiting for /start-worm or auto-join.");
                    return;
                }

                // If a track is already playing, do nothing — the TrackEnded
                // handler will pick up the next track when the current one ends.
                if (player.State == PlayerState.Playing || player.State == PlayerState.Paused)
                {
                    return;
                }

                await PlayNextAsync(player);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: swallow.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling TrackQueued event.");
            }
        }, ct);
    }

    private Task OnLavalinkTrackEndedAsync(object sender, TrackEndedEventArgs e)
    {
        if (e.Player.GuildId != _guildId) return Task.CompletedTask;

        var ct = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // TTS pre-roll just ended — signal the awaiter and bail. We do
                // NOT log history or fire our events for TTS. We identify the
                // TTS event by the staged track identifier (not just by
                // _ttsCompletion being non-null) so that a *late* event
                // arriving after the 30s timeout already fired (and cleared
                // _ttsCompletion) is still recognized and ignored instead of
                // being treated as the music track ending.
                string? endedIdentifier = e.Track?.Identifier;
                bool isTtsEvent;
                TaskCompletionSource? ttsTcs = null;
                lock (_stateLock)
                {
                    isTtsEvent = _currentTtsIdentifier != null
                        && string.Equals(_currentTtsIdentifier, endedIdentifier, StringComparison.Ordinal);
                    if (isTtsEvent)
                    {
                        _currentTtsIdentifier = null;
                        ttsTcs = _ttsCompletion;
                        _ttsCompletion = null;
                    }
                    else if (_ttsCompletion != null)
                    {
                        // Fallback: no identifier match but a TTS awaiter is still
                        // pending (e.g. Lavalink didn't echo the track identifier).
                        // Treat as the TTS ending so the music track can proceed.
                        isTtsEvent = true;
                        ttsTcs = _ttsCompletion;
                        _ttsCompletion = null;
                        _currentTtsIdentifier = null;
                    }
                }

                if (isTtsEvent)
                {
                    ttsTcs?.TrySetResult();
                    return;
                }

                // StopAsync() (the /stop-worm / leave / deregister path) asked us
                // to suppress auto-advance. Consume and reset the flag here so the
                // next real stop (e.g. /skip) advances normally.
                bool halt;
                lock (_stateLock)
                {
                    halt = _haltAfterTrackEnded;
                    _haltAfterTrackEnded = false;
                }

                QueueItem? ended;
                DateTimeOffset startedAt;
                lock (_stateLock)
                {
                    ended = _currentTrack;
                    startedAt = _trackStartedAt;
                    _currentTrack = null;
                    _isPaused = false;
                    RebuildStateLocked();
                }

                if (ended != null)
                {
                    bool skipped = e.Reason == TrackEndReason.Stopped || e.Reason == TrackEndReason.Replaced;
                    bool failed = e.Reason == TrackEndReason.LoadFailed;
                    int playedSec = (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds;

                    await LogPlayHistoryAsync(ended, playedSec, skipped, failed, failed ? "Lavalink load failed" : null);

                    if (failed) TrackFailed?.Invoke(ended, "Lavalink load failed");
                    TrackEnded?.Invoke(ended, skipped, failed ? "Lavalink load failed" : null);
                }

                if (halt)
                {
                    // Explicit stop: persist the idle state and do NOT advance.
                    await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                    return;
                }

                // Advance to the next track only if the end reason allows it.
                // TrackEndReason.Stopped (explicit /skip) and Replaced still let
                // us advance — those user actions semantically mean "next".
                if (e.MayStartNext || e.Reason == TrackEndReason.Stopped || e.Reason == TrackEndReason.Replaced)
                {
                    if (e.Player is LavalinkPlayer player)
                    {
                        await PlayNextAsync(player);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: swallow.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Lavalink TrackEnded handler.");
            }
        }, ct);

        return Task.CompletedTask;
    }

    private async Task PlayNextAsync(LavalinkPlayer player)
    {
        int consecutiveFailures = 0;

        while (true)
        {
            var next = await _queueManager.DequeueAsync();
            if (next == null)
            {
                _logger.LogInformation("Queue is empty; player idle.");
                lock (_stateLock)
                {
                    _currentTrack = null;
                    _isPaused = false;
                    RebuildStateLocked();
                }
                await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                return;
            }

            // Pre-track hook: DJ commentary preroll or null.
            TtsPreroll? preroll = null;
            if (_preTrackHook != null)
            {
                try
                {
                    preroll = await _preTrackHook(next, _shutdown.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pre-track hook (DJ commentary) failed; continuing to music.");
                }
            }

            // Play TTS pre-roll first if commentary was generated, then run its
            // cleanup callback so DJEngine can remove the staged .mp3.
            if (preroll != null)
            {
                try
                {
                    var ttsTrack = await _audioService.Tracks.LoadTrackAsync(preroll.Url, TrackSearchMode.None);
                    if (ttsTrack != null)
                    {
                        TaskCompletionSource tcs;
                        lock (_stateLock)
                        {
                            _ttsCompletion = tcs = new TaskCompletionSource();
                            // Record the staged TTS track's identifier so a late
                            // TrackEnded event (after the 30s timeout below fires
                            // and clears _ttsCompletion) can still be recognized as
                            // a TTS event and ignored rather than advancing the
                            // queue a second time.
                            _currentTtsIdentifier = ttsTrack.Identifier;
                        }
                        await _transitions.PrepareForPrerollAsync(player);
                        await player.PlayAsync(ttsTrack);
                        // Cap the wait: if Lavalink misses the TrackEnded event for
                        // the TTS clip (reconnect, dropped event), the player would
                        // otherwise hang the queue indefinitely.
                        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                        if (completed != tcs.Task)
                        {
                            _logger.LogWarning("TTS preroll did not complete within 30s; clearing state and continuing.");
                            lock (_stateLock)
                            {
                                if (_ttsCompletion == tcs) _ttsCompletion = null;
                                // NOTE: _currentTtsIdentifier is intentionally left
                                // set so a late TTS TrackEnded is still identified
                                // and ignored. It is cleared when the music track
                                // starts (below) or when the matching event arrives.
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Lavalink could not load TTS URL: {Url}", preroll.Url);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to play TTS pre-roll; continuing to music.");
                    lock (_stateLock) { _ttsCompletion = null; _currentTtsIdentifier = null; }
                }
                finally
                {
                    try { await preroll.OnConsumedAsync(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "TTS cleanup callback threw."); }
                }
            }

            // Now play the actual music track.
            var query = BuildTrackQuery(next);
            LavalinkTrack? musicTrack;
            try
            {
                musicTrack = await _audioService.Tracks.LoadTrackAsync(query, TrackSearchMode.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lavalink threw while loading track: {Title}", next.Title);
                TrackFailed?.Invoke(next, $"Load error: {ex.Message}");
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveLoadFailures)
                {
                    _logger.LogCritical("Hit {Cap} consecutive Lavalink load failures; halting playback and going idle.", MaxConsecutiveLoadFailures);
                    lock (_stateLock)
                    {
                        _currentTrack = null;
                        _isPaused = false;
                        RebuildStateLocked();
                    }
                    await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                    return;
                }
                continue;
            }

            if (musicTrack == null)
            {
                _logger.LogWarning("Lavalink returned no result for query: {Query}", query);
                TrackFailed?.Invoke(next, "Track not found via Lavalink.");
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveLoadFailures)
                {
                    _logger.LogCritical("Hit {Cap} consecutive Lavalink load failures; halting playback and going idle.", MaxConsecutiveLoadFailures);
                    lock (_stateLock)
                    {
                        _currentTrack = null;
                        _isPaused = false;
                        RebuildStateLocked();
                    }
                    await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                    return;
                }
                continue;
            }

            consecutiveFailures = 0;

            lock (_stateLock)
            {
                _currentTrack = next;
                _trackStartedAt = DateTimeOffset.UtcNow;
                _isPaused = false;
                // The music track is now authoritative; a late TTS TrackEnded
                // (if any) should not be matched against the staged TTS id.
                _currentTtsIdentifier = null;
                // Track the bot's current voice channel so resume-after-restart
                // state can report it (RebuildStateLocked reads this field).
                _voiceChannelId = player.VoiceChannelId;
                RebuildStateLocked();
            }

            var duration = next.DurationSeconds.HasValue
                ? TimeSpan.FromSeconds(next.DurationSeconds.Value)
                : (TimeSpan?)null;
            await _transitions.PlayMusicAsync(player, duration, () => player.PlayAsync(musicTrack));
            TrackStarted?.Invoke(next);

            await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);

            // Metrics: tracks_completed gets incremented on TrackEnded (Finished),
            // not here. Here we just record a per-user "request played" beat by
            // bumping the source-type counter when we actually start it.
            try
            {
                string sourceMetric = next.SourceType switch
                {
                    "youtube" => "requests_youtube",
                    "soundcloud" => "requests_soundcloud",
                    "mp3_upload" => "requests_mp3_upload",
                    _ => "requests_search"
                };
                await _metricsRepository.IncrementGlobalMetricAsync(_guildIdStr, sourceMetric, 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to increment source-type metric.");
            }

            return;
        }
    }

    private static string BuildTrackQuery(QueueItem item)
    {
        // SourceId stored in DB is either a YouTube video id or a full URL
        // (SoundCloud, attachment HTTP URL, etc.). For YouTube ids we reconstruct
        // the canonical URL so Lavalink's HTTP/youtube source plugins identify it.
        if (item.SourceType.Equals("youtube", StringComparison.OrdinalIgnoreCase) && !item.SourceId.StartsWith("http"))
        {
            return $"https://www.youtube.com/watch?v={item.SourceId}";
        }
        return item.SourceId;
    }

    private async ValueTask<LavalinkPlayer?> TryGetPlayerAsync()
    {
        return await _audioService.Players.GetPlayerAsync<LavalinkPlayer>(_guildId);
    }

    public virtual PlaybackState State
    {
        get
        {
            lock (_stateLock)
            {
                return _cachedState;
            }
        }
    }

    private void RebuildStateLocked()
    {
        var track = _currentTrack;
        var paused = _isPaused;

        // Position requires a sync player handle which we don't safely have.
        // Leave as 0 for v1; the /queue display shows "0:00 / 3:45" which
        // is acceptable while we ship.
        _cachedState = new PlaybackState
        {
            IsPlaying = track != null && !paused,
            IsPaused = paused,
            CurrentSourceType = track?.SourceType,
            CurrentSourceId = track?.SourceId,
            CurrentTitle = track?.Title,
            CurrentArtist = track?.Artist,
            CurrentDurationSeconds = track?.DurationSeconds,
            CurrentRequestedByUserId = track?.RequestedByUserId,
            CurrentRequestedByDisplayName = track?.RequestedByDisplayName,
            CurrentPositionMs = 0,
            VoiceChannelId = _voiceChannelId?.ToString(),
            VoiceGuildId = _guildId.ToString(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private PlaybackState BuildIdleState() => new()
    {
        IsPlaying = false,
        IsPaused = false,
        VoiceChannelId = null,
        VoiceGuildId = _guildId.ToString(),
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public async Task PauseAsync()
    {
        var player = await TryGetPlayerAsync();
        if (player == null) return;
        await player.PauseAsync();
        lock (_stateLock) { _isPaused = true; RebuildStateLocked(); }
        PlaybackPaused?.Invoke();
    }

    public async Task ResumeAsync()
    {
        var player = await TryGetPlayerAsync();
        if (player == null) return;
        await player.ResumeAsync();
        lock (_stateLock) { _isPaused = false; RebuildStateLocked(); }
        PlaybackResumed?.Invoke();
    }

    public async Task SkipAsync()
    {
        var player = await TryGetPlayerAsync();
        if (player == null) return;
        // Skip is an explicit user action: clear any stale halt flag left by a
        // prior StopAsync() whose TrackEnded never fired (e.g. stop called on an
        // idle player) so this skip's own TrackEnded(Stopped) advances.
        lock (_stateLock) { _haltAfterTrackEnded = false; }
        // StopAsync triggers TrackEnded with Reason=Stopped, which our handler
        // treats as "advance to next."
        await player.StopAsync();
    }

    public async Task PreviousAsync()
    {
        var history = await _historyRepository.GetRecentHistoryAsync(_guildIdStr, 1);
        if (history.Count == 0)
        {
            throw new InvalidOperationException("No track has been played yet in this session.");
        }

        var last = history[0];
        var requeueItem = new QueueItem
        {
            SourceType = last.SourceType,
            SourceId = last.SourceId,
            Title = last.Title,
            Artist = last.Artist,
            DurationSeconds = last.DurationSeconds,
            RequestedByUserId = last.RequestedByUserId,
            RequestedByDisplayName = last.RequestedByDisplayName,
            GuildId = last.GuildId
        };

        await _queueManager.RequeueFrontAsync(requeueItem);
        await SkipAsync();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var player = await TryGetPlayerAsync();
        if (player == null) throw new InvalidOperationException("No track is currently playing.");
        await player.SeekAsync(position);
    }

    public async Task StopAsync()
    {
        var player = await TryGetPlayerAsync();

        // Only arm the halt flag when a track is actually playing — that's the
        // only case where player.StopAsync() will produce a TrackEnded(Stopped)
        // event that the handler would (wrongly) auto-advance from. If nothing
        // is playing, no event fires, so arming the flag here would just linger
        // and suppress the next SkipAsync's advance.
        bool hasCurrentTrack;
        lock (_stateLock)
        {
            hasCurrentTrack = _currentTrack != null;
            if (hasCurrentTrack) _haltAfterTrackEnded = true;
        }

        if (player != null)
        {
            await player.StopAsync();
        }
        lock (_stateLock)
        {
            _currentTrack = null;
            _isPaused = false;
            // Clear any in-flight TTS bookkeeping so a stale event can't fire
            // the awaiter or be matched after we've gone idle.
            _ttsCompletion = null;
            _currentTtsIdentifier = null;
            // If we did NOT have a current track, no TrackEnded will fire to
            // consume the flag — make sure it's cleared so it can't suppress a
            // future skip. (If we did, the handler will reset it.)
            if (!hasCurrentTrack) _haltAfterTrackEnded = false;
            RebuildStateLocked();
        }
    }

    /// <summary>
    /// Called by VoiceManager after the bot joins a voice channel. If a player
    /// is now available and nothing is playing, dequeue and start. No-op if
    /// there's no player yet (still connecting) or playback is already active.
    /// </summary>
    public async Task MaybeStartAsync()
    {
        var player = await TryGetPlayerAsync();
        if (player == null) return;
        if (player.State == PlayerState.Playing || player.State == PlayerState.Paused) return;
        await PlayNextAsync(player);
    }

    private async Task LogPlayHistoryAsync(QueueItem track, int playedSeconds, bool skipped, bool failed, string? failureReason = null)
    {
        try
        {
            var entry = new PlayHistoryEntry
            {
                PlayedAt = DateTimeOffset.UtcNow,
                SourceType = track.SourceType,
                SourceId = track.SourceId,
                Title = track.Title,
                Artist = track.Artist,
                DurationSeconds = track.DurationSeconds,
                PlayedSeconds = playedSeconds,
                RequestedByUserId = track.RequestedByUserId,
                RequestedByDisplayName = track.RequestedByDisplayName,
                Skipped = skipped,
                Failed = failed,
                FailureReason = failureReason,
                GuildId = track.GuildId
            };

            await _historyRepository.AddHistoryEntryAsync(entry, _config.Persistence.HistoryRetentionCount);

            var metricsBatch = new List<MetricIncrement>();

            if (failed)
            {
                metricsBatch.Add(new MetricIncrement("tracks_failed", 1));
            }
            else if (!skipped)
            {
                metricsBatch.Add(new MetricIncrement("tracks_completed", 1, track.RequestedByUserId, track.RequestedByDisplayName));
                metricsBatch.Add(new MetricIncrement("tracks_completed", 1));
            }

            if (playedSeconds > 0)
            {
                metricsBatch.Add(new MetricIncrement("listening_seconds", playedSeconds, track.RequestedByUserId, track.RequestedByDisplayName));
                metricsBatch.Add(new MetricIncrement("listening_seconds", playedSeconds));
            }

            if (metricsBatch.Count > 0)
            {
                await _metricsRepository.IncrementBatchAsync(_guildIdStr, metricsBatch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log play history.");
        }
    }

    public void Dispose()
    {
        _queueManager.TrackQueued -= OnTrackQueued;
        _audioService.TrackEnded -= OnLavalinkTrackEndedAsync;
        _transitions.Cancel();
    }
}
