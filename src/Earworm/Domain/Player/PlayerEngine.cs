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
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly CancellationToken _lifetimeToken;
    private CancellationTokenSource _advanceCts;
    private int _disposed;

    private readonly ulong _guildId;
    private readonly string _guildIdStr;
    private readonly object _stateLock = new();
    // All paths that can advance the guild's queue share this gate. Queue
    // notifications and Lavalink end events can arrive concurrently; without a
    // per-guild gate they can both observe idle and dequeue different tracks.
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private readonly SemaphoreSlim _stopGate = new(1, 1);

    private QueueItem? _currentTrack;
    private string? _currentMusicIdentifier;
    private long _playbackGeneration;
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

    // Lavalink event callbacks are offloaded from its shared receive loop. A
    // FIFO receipt record captures which playback generation an event belongs
    // to before that offloaded work can be reordered with a restart of the same
    // identifier. TrackStarted is the ordering fence that retires lost older
    // acknowledgements.
    private const int MaxStopExpectations = 32;
    private readonly List<StopExpectation> _stopExpectations = new();
    private readonly List<StartExpectation> _startExpectations = new();
    private bool _retired;
    private bool _restoreInProgress;

    private enum StopIntent
    {
        Halt,
        Advance
    }

    private sealed record StopExpectation(
        long Generation,
        string Identifier,
        StopIntent Intent,
        TaskCompletionSource? Completion = null,
        TaskCompletionSource? Receipt = null);

    private sealed record StartExpectation(long Generation, string Identifier);

    private readonly record struct TrackEndReceipt(
        long? Generation,
        StopIntent? Intent,
        TaskCompletionSource? Completion,
        bool IsTts = false,
        TaskCompletionSource? TtsCompletion = null);

    public virtual event Action<QueueItem>? TrackStarted;
    public virtual event Action<QueueItem, bool, string?>? TrackEnded; // (track, skipped, failureReason)
    public virtual event Action<QueueItem, string>? TrackFailed;
    public virtual event Action<string>? PlaybackBecameIdle;
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
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        _lifetimeToken = _lifetimeCts.Token;
        _advanceCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);

        _guildIdStr = guildId;
        if (!ulong.TryParse(guildId, out _guildId))
        {
            throw new InvalidOperationException($"PlayerEngine requires a valid numeric guild id; got '{guildId}'.");
        }

        _cachedState = BuildIdleState();

        _queueManager.TrackQueued += OnTrackQueued;
        _audioService.TrackStarted += OnLavalinkTrackStartedAsync;
        _audioService.TrackEnded += OnLavalinkTrackEndedAsync;
    }

    private void OnTrackQueued(QueueItem item)
    {
        var ct = _lifetimeToken;
        _ = HandleTrackQueuedAsync(ct);
    }

    private async Task HandleTrackQueuedAsync(CancellationToken ct)
    {
        try
        {
            await _playbackGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                lock (_stateLock)
                {
                    if (_retired) return;
                }

                var player = await TryGetPlayerAsync();
                if (player == null)
                {
                    _logger.LogInformation("Track queued but no active voice player; waiting for /start-worm or auto-join.");
                    return;
                }

                bool hasCurrentTrack;
                lock (_stateLock) { hasCurrentTrack = _currentTrack != null; }
                if (hasCurrentTrack || player.State == PlayerState.Playing || player.State == PlayerState.Paused)
                {
                    return;
                }

                await PlayNextCoreAsync(player);
            }
            finally
            {
                _playbackGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal process shutdown or an explicit stop generation change.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling TrackQueued event.");
        }
    }

    private Task OnLavalinkTrackStartedAsync(object sender, TrackStartedEventArgs e)
    {
        if (e.Player.GuildId != _guildId) return Task.CompletedTask;

        lock (_stateLock)
        {
            string? startedIdentifier = e.Track?.Identifier;
            int startIndex = _startExpectations.FindIndex(expectation =>
                string.Equals(expectation.Identifier, startedIdentifier, StringComparison.Ordinal));
            if (startIndex < 0) return Task.CompletedTask;

            long fenceGeneration = _startExpectations[startIndex].Generation;
            _startExpectations.RemoveAt(startIndex);

            // Lavalink events arrive in websocket order. Once the server has
            // announced a new (music or TTS) track start, an unmatched stop for
            // an older generation was lost and cannot legitimately arrive
            // later. Events already received were classified synchronously by
            // OnLavalinkTrackEndedAsync before their work was offloaded.
            for (int i = _stopExpectations.Count - 1; i >= 0; i--)
            {
                if (_stopExpectations[i].Generation >= fenceGeneration) continue;
                _stopExpectations[i].Completion?.TrySetResult();
                _stopExpectations[i].Receipt?.TrySetResult();
                _stopExpectations.RemoveAt(i);
            }
            _startExpectations.RemoveAll(expectation => expectation.Generation < fenceGeneration);
        }

        return Task.CompletedTask;
    }

    private Task OnLavalinkTrackEndedAsync(object sender, TrackEndedEventArgs e)
    {
        if (e.Player.GuildId != _guildId) return Task.CompletedTask;

        var ct = _lifetimeToken;
        TrackEndReceipt receipt;
        lock (_stateLock) receipt = ClassifyTrackEndReceiptLocked(e);

        // Lavalink4NET awaits TrackEnded subscribers on its shared payload
        // receive loop. Advancing a queue can include DB writes, remote track
        // resolution, and a TTS preroll, so never hold that global loop while a
        // single guild advances. Receipt classification above is intentionally
        // synchronous: it preserves websocket event order even if Task.Run
        // schedules two end handlers out of order.
        _ = Task.Run(() => HandleLavalinkTrackEndedAsync(e, receipt, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    private TrackEndReceipt ClassifyTrackEndReceiptLocked(TrackEndedEventArgs e)
    {
        string? endedIdentifier = e.Track?.Identifier;

        // Exact TTS identity takes precedence, but a pending TTS awaiter alone
        // must not swallow an older music stop with a different identifier.
        if (_currentTtsIdentifier is not null
            && string.Equals(_currentTtsIdentifier, endedIdentifier, StringComparison.Ordinal))
        {
            var ttsCompletion = _ttsCompletion;
            _currentTtsIdentifier = null;
            _ttsCompletion = null;
            return new TrackEndReceipt(null, null, null, IsTts: true, ttsCompletion);
        }

        if (endedIdentifier is not null)
        {
            for (int i = 0; i < _stopExpectations.Count; i++)
            {
                var expectation = _stopExpectations[i];
                if (!string.Equals(expectation.Identifier, endedIdentifier, StringComparison.Ordinal)) continue;

                _stopExpectations.RemoveAt(i);
                expectation.Receipt?.TrySetResult();
                _startExpectations.RemoveAll(start => start.Generation <= expectation.Generation);
                return new TrackEndReceipt(
                    expectation.Generation,
                    expectation.Intent,
                    expectation.Completion);
            }
        }

        if (endedIdentifier is null && _ttsCompletion is not null)
        {
            var ttsCompletion = _ttsCompletion;
            _currentTtsIdentifier = null;
            _ttsCompletion = null;
            return new TrackEndReceipt(null, null, null, IsTts: true, ttsCompletion);
        }

        bool matchesCurrent = _currentTrack is not null
            && (_currentMusicIdentifier is null
                || endedIdentifier is null
                || string.Equals(_currentMusicIdentifier, endedIdentifier, StringComparison.Ordinal));
        if (!matchesCurrent) return default;

        _startExpectations.RemoveAll(start => start.Generation <= _playbackGeneration);
        return new TrackEndReceipt(_playbackGeneration, Intent: null, Completion: null);
    }

    private void AddStopExpectationLocked(StopExpectation expectation)
    {
        if (_stopExpectations.Count >= MaxStopExpectations)
        {
            var retired = _stopExpectations[0];
            _stopExpectations.RemoveAt(0);
            retired.Completion?.TrySetResult();
            retired.Receipt?.TrySetResult();
        }
        _stopExpectations.Add(expectation);
    }

    private void AddStartExpectationLocked(StartExpectation expectation)
    {
        if (_startExpectations.Count >= MaxStopExpectations)
        {
            _startExpectations.RemoveAt(0);
        }
        _startExpectations.Add(expectation);
    }

    private async Task HandleLavalinkTrackEndedAsync(
        TrackEndedEventArgs e,
        TrackEndReceipt receipt,
        CancellationToken ct)
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
            if (receipt.IsTts)
            {
                receipt.TtsCompletion?.TrySetResult();
                return;
            }

            await _playbackGate.WaitAsync(ct);
            try
            {
                QueueItem? ended;
                DateTimeOffset startedAt;
                bool halt;
                bool staleEvent;
                string? currentIdentifier;
                long currentGeneration;
                lock (_stateLock)
                {
                    ended = _currentTrack;
                    startedAt = _trackStartedAt;
                    currentIdentifier = _currentMusicIdentifier;
                    currentGeneration = _playbackGeneration;

                    staleEvent = receipt.Generation is null
                        || receipt.Generation.Value != currentGeneration
                        || ended is null
                        || (currentIdentifier is not null
                            && endedIdentifier is not null
                            && !string.Equals(currentIdentifier, endedIdentifier, StringComparison.Ordinal));
                    halt = receipt.Intent == StopIntent.Halt;

                    if (!staleEvent)
                    {
                        _stopExpectations.RemoveAll(expectation => expectation.Generation == currentGeneration);
                        _startExpectations.RemoveAll(expectation => expectation.Generation == currentGeneration);
                        _currentTrack = null;
                        _currentMusicIdentifier = null;
                        _isPaused = false;
                        RebuildStateLocked();
                    }
                }

                if (staleEvent)
                {
                    _logger.LogWarning(
                        "Ignoring TrackEnded event for receipt generation {ReceiptGeneration}; current generation is {CurrentGeneration} ({EndedIdentifier}/{CurrentIdentifier}).",
                        receipt.Generation,
                        currentGeneration,
                        endedIdentifier,
                        currentIdentifier);
                    return;
                }

                if (ended != null)
                {
                    bool skipped = e.Reason == TrackEndReason.Stopped || e.Reason == TrackEndReason.Replaced;
                    bool failed = e.Reason == TrackEndReason.LoadFailed;
                    int playedSec = (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds;

                    await LogPlayHistoryAsync(ended, playedSec, skipped, failed, failed ? "Lavalink load failed" : null);

                    if (failed) NotifyTrackFailed(ended, "Lavalink load failed");
                    NotifyTrackEnded(ended, skipped, failed ? "Lavalink load failed" : null);
                }

                if (halt)
                {
                    await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                    return;
                }

                if (e.MayStartNext || e.Reason == TrackEndReason.Stopped || e.Reason == TrackEndReason.Replaced)
                {
                    if (e.Player is LavalinkPlayer player)
                    {
                        await PlayNextCoreAsync(player);
                    }
                }
            }
            finally
            {
                _playbackGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal process shutdown or an explicit stop generation change.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Lavalink TrackEnded handler.");
        }
        finally
        {
            receipt.Completion?.TrySetResult();
        }
    }

    private async Task PlayNextAsync(LavalinkPlayer player)
    {
        await _playbackGate.WaitAsync(_lifetimeToken);
        try
        {
            await PlayNextCoreAsync(player);
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    private async Task PlayNextCoreAsync(LavalinkPlayer player)
    {
        int consecutiveFailures = 0;
        CancellationToken advanceToken;
        lock (_stateLock) advanceToken = _advanceCts.Token;
        QueueItem? pendingTrack = null;
        bool playbackStarted = false;
        long attemptGeneration = 0;
        string? attemptedMusicIdentifier = null;

        try
        {
            while (true)
            {
                advanceToken.ThrowIfCancellationRequested();
                var next = await _queueManager.DequeueAsync();
                pendingTrack = next;
                playbackStarted = false;
                attemptedMusicIdentifier = null;
                advanceToken.ThrowIfCancellationRequested();
                if (next == null)
                {
                    _logger.LogInformation("Queue is empty; player idle.");
                    lock (_stateLock)
                    {
                        _currentTrack = null;
                        _currentMusicIdentifier = null;
                        _isPaused = false;
                        RebuildStateLocked();
                    }
                    NotifyPlaybackBecameIdle();
                    await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                    return;
                }

                lock (_stateLock) attemptGeneration = ++_playbackGeneration;

                // Pre-track hook: DJ commentary preroll or null.
                TtsPreroll? preroll = null;
                if (_preTrackHook != null)
                {
                    try
                    {
                        preroll = await _preTrackHook(next, advanceToken);
                    }
                    catch (OperationCanceledException) when (advanceToken.IsCancellationRequested)
                    {
                        throw;
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
                    StartExpectation? ttsStartExpectation = null;
                    try
                    {
                        var ttsTrack = await _audioService.Tracks.LoadTrackAsync(
                            preroll.Url, TrackSearchMode.None, cancellationToken: advanceToken);
                        if (ttsTrack != null)
                        {
                            TaskCompletionSource tcs;
                            lock (_stateLock)
                            {
                                _ttsCompletion = tcs = new TaskCompletionSource();
                                ttsStartExpectation = new StartExpectation(attemptGeneration, ttsTrack.Identifier);
                                AddStartExpectationLocked(ttsStartExpectation);
                                // Record the staged TTS track's identifier so a late
                                // TrackEnded event (after the 30s timeout below fires
                                // and clears _ttsCompletion) can still be recognized as
                                // a TTS event and ignored rather than advancing the
                                // queue a second time.
                                _currentTtsIdentifier = ttsTrack.Identifier;
                            }
                            await _transitions.PrepareForPrerollAsync(player, advanceToken);
                            await player.PlayAsync(ttsTrack, cancellationToken: advanceToken);
                            // Cap the wait: if Lavalink misses the TrackEnded event for
                            // the TTS clip (reconnect, dropped event), the player would
                            // otherwise hang the queue indefinitely.
                            var completed = await Task.WhenAny(
                                tcs.Task,
                                Task.Delay(TimeSpan.FromSeconds(30), advanceToken));
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
                    catch (OperationCanceledException) when (advanceToken.IsCancellationRequested)
                    {
                        lock (_stateLock)
                        {
                            if (ttsStartExpectation is not null) _startExpectations.Remove(ttsStartExpectation);
                        }
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to play TTS pre-roll; continuing to music.");
                        lock (_stateLock)
                        {
                            if (ttsStartExpectation is not null) _startExpectations.Remove(ttsStartExpectation);
                            _ttsCompletion = null;
                            _currentTtsIdentifier = null;
                        }
                    }
                    finally
                    {
                        try { await preroll.OnConsumedAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "TTS cleanup callback threw."); }
                    }
                }

                advanceToken.ThrowIfCancellationRequested();

                // Now play the actual music track.
                var query = BuildTrackQuery(next);
                LavalinkTrack? musicTrack;
                try
                {
                    musicTrack = await _audioService.Tracks.LoadTrackAsync(
                        query, TrackSearchMode.None, cancellationToken: advanceToken);
                }
                catch (OperationCanceledException) when (advanceToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lavalink threw while loading track: {Title}", next.Title);
                    pendingTrack = null; // load failures intentionally consume this item
                    NotifyTrackFailed(next, $"Load error: {ex.Message}");
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaxConsecutiveLoadFailures)
                    {
                        advanceToken.ThrowIfCancellationRequested();
                        _logger.LogCritical("Hit {Cap} consecutive Lavalink load failures; halting playback and going idle.", MaxConsecutiveLoadFailures);
                        lock (_stateLock)
                        {
                            _currentTrack = null;
                            _currentMusicIdentifier = null;
                            _isPaused = false;
                            RebuildStateLocked();
                        }
                        NotifyPlaybackBecameIdle();
                        await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                        return;
                    }
                    continue;
                }

                if (musicTrack == null)
                {
                    _logger.LogWarning("Lavalink returned no result for query: {Query}", query);
                    pendingTrack = null; // unresolved items intentionally leave the queue
                    NotifyTrackFailed(next, "Track not found via Lavalink.");
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaxConsecutiveLoadFailures)
                    {
                        advanceToken.ThrowIfCancellationRequested();
                        _logger.LogCritical("Hit {Cap} consecutive Lavalink load failures; halting playback and going idle.", MaxConsecutiveLoadFailures);
                        lock (_stateLock)
                        {
                            _currentTrack = null;
                            _currentMusicIdentifier = null;
                            _isPaused = false;
                            RebuildStateLocked();
                        }
                        NotifyPlaybackBecameIdle();
                        await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                        return;
                    }
                    continue;
                }

                consecutiveFailures = 0;
                advanceToken.ThrowIfCancellationRequested();

                var musicStartExpectation = new StartExpectation(attemptGeneration, musicTrack.Identifier);
                attemptedMusicIdentifier = musicTrack.Identifier;
                lock (_stateLock)
                {
                    _currentTrack = next;
                    _currentMusicIdentifier = musicTrack.Identifier;
                    AddStartExpectationLocked(musicStartExpectation);
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
                await _transitions.PlayMusicAsync(
                    player,
                    duration,
                    () => player.PlayAsync(musicTrack, cancellationToken: advanceToken),
                    advanceToken);
                playbackStarted = true;
                pendingTrack = null;
                try { TrackStarted?.Invoke(next); }
                catch (Exception ex) { _logger.LogError(ex, "TrackStarted subscriber failed."); }

                try
                {
                    await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                }
                catch (Exception ex)
                {
                    // Lavalink is already playing. Persistence failure must not
                    // hide TrackStarted or make callers roll back live audio.
                    _logger.LogError(ex, "Failed to persist started playback state.");
                }

                return;
            }
        }
        catch (OperationCanceledException) when (advanceToken.IsCancellationRequested)
        {
            await RecoverInterruptedTrackAsync(pendingTrack, playbackStarted);
            throw;
        }
        catch (Exception ex) when (pendingTrack != null && !playbackStarted)
        {
            _logger.LogError(ex, "Failed to start dequeued track {Title}; returning it to the queue.", pendingTrack.Title);
            await StopAmbiguousPlaybackStartAsync(player, attemptGeneration, attemptedMusicIdentifier);
            await RecoverInterruptedTrackAsync(pendingTrack, playbackStarted: false);
            if (!advanceToken.IsCancellationRequested)
            {
                NotifyPlaybackBecameIdle();
                NotifyTrackFailed(pendingTrack, "Lavalink play request failed.");
            }
        }
    }

    private void NotifyPlaybackBecameIdle()
    {
        lock (_stateLock)
        {
            if (_retired || _lifetimeToken.IsCancellationRequested) return;
        }

        try { PlaybackBecameIdle?.Invoke(_guildIdStr); }
        catch (Exception ex) { _logger.LogError(ex, "PlaybackBecameIdle subscriber failed."); }
    }

    private void NotifyTrackFailed(QueueItem track, string reason)
    {
        var subscribers = TrackFailed;
        if (subscribers is null) return;
        foreach (Action<QueueItem, string> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(track, reason); }
            catch (Exception ex) { _logger.LogError(ex, "TrackFailed subscriber failed."); }
        }
    }

    private void NotifyTrackEnded(QueueItem track, bool skipped, string? failureReason)
    {
        var subscribers = TrackEnded;
        if (subscribers is null) return;
        foreach (Action<QueueItem, bool, string?> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(track, skipped, failureReason); }
            catch (Exception ex) { _logger.LogError(ex, "TrackEnded subscriber failed."); }
        }
    }

    private async Task StopAmbiguousPlaybackStartAsync(
        LavalinkPlayer player,
        long generation,
        string? identifier)
    {
        if (identifier is null) return;

        // A failed REST call does not prove Lavalink rejected the play command;
        // the server may have accepted it before the transport failed. Fence a
        // possible late start/end pair, then best-effort stop so the item can be
        // safely returned to Earworm's queue without untracked audio continuing.
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectation = new StopExpectation(
            generation,
            identifier,
            StopIntent.Halt,
            Receipt: receipt);
        lock (_stateLock)
        {
            AddStopExpectationLocked(expectation);
        }

        try
        {
            await player.StopAsync(cancellationToken: _lifetimeToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop an ambiguously accepted Lavalink play request.");
        }

        await Task.WhenAny(
            receipt.Task,
            Task.Delay(TimeSpan.FromSeconds(2), _lifetimeToken));

        bool expired;
        lock (_stateLock)
        {
            bool startStillPending = _startExpectations.Exists(start =>
                start.Generation == generation
                && string.Equals(start.Identifier, identifier, StringComparison.Ordinal));
            expired = startStillPending && _stopExpectations.Remove(expectation);
            if (expired)
            {
                // Neither start nor stop was observed. Treat the failed play as
                // rejected so a same-identifier retry cannot be consumed by
                // this abandoned generation's receipts. If TrackStarted was
                // observed, retain the stop tombstone until its TrackEnded or a
                // later TrackStarted fence arrives.
                _startExpectations.RemoveAll(start =>
                    start.Generation == generation
                    && string.Equals(start.Identifier, identifier, StringComparison.Ordinal));
            }
        }
        if (expired)
        {
            _logger.LogWarning("No Lavalink receipt followed an ambiguously failed play; retired generation {Generation}.", generation);
        }
    }

    private async Task RecoverInterruptedTrackAsync(QueueItem? pendingTrack, bool playbackStarted)
    {
        lock (_stateLock)
        {
            if (pendingTrack != null && ReferenceEquals(_currentTrack, pendingTrack))
            {
                _currentTrack = null;
                _currentMusicIdentifier = null;
                _isPaused = false;
                RebuildStateLocked();
            }
        }

        if (pendingTrack != null && !playbackStarted)
        {
            // Do not emit TrackQueued here: on a failed play request it would
            // immediately retry the same front item and create a tight loop.
            try { await _queueManager.RequeueFrontAsync(pendingTrack, notify: false); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to restore interrupted track {Title} to the queue.", pendingTrack.Title); }
        }

        try { await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to persist idle state after interrupted playback start."); }
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

    private void ThrowIfRetired()
    {
        lock (_stateLock)
        {
            if (_retired) throw new InvalidOperationException("This player engine has been retired.");
        }
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
        await _stopGate.WaitAsync(_lifetimeToken);
        try
        {
            ThrowIfRetired();
            var player = await TryGetPlayerAsync();
            if (player == null) return;
            await player.PauseAsync(cancellationToken: _lifetimeToken);
            lock (_stateLock) { _isPaused = true; RebuildStateLocked(); }
            PlaybackPaused?.Invoke();
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _stopGate.WaitAsync(_lifetimeToken);
        try
        {
            ThrowIfRetired();
            var player = await TryGetPlayerAsync();
            if (player == null) return;
            await player.ResumeAsync(cancellationToken: _lifetimeToken);
            lock (_stateLock) { _isPaused = false; RebuildStateLocked(); }
            PlaybackResumed?.Invoke();
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async Task SkipAsync()
    {
        await _stopGate.WaitAsync(_lifetimeToken);
        try
        {
            await SkipCoreAsync();
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task SkipCoreAsync()
    {
        ThrowIfRetired();
        var player = await TryGetPlayerAsync();
        if (player == null) return;
        lock (_stateLock)
        {
            if (_currentTrack is not null && _currentMusicIdentifier is not null)
            {
                AddStopExpectationLocked(new StopExpectation(
                    _playbackGeneration,
                    _currentMusicIdentifier,
                    StopIntent.Advance));
            }
        }
        await player.StopAsync(cancellationToken: _lifetimeToken);
    }

    public async Task PreviousAsync()
    {
        await _stopGate.WaitAsync(_lifetimeToken);
        try
        {
            ThrowIfRetired();
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
            await SkipCoreAsync();
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        await _stopGate.WaitAsync(_lifetimeToken);
        try
        {
            ThrowIfRetired();
            var player = await TryGetPlayerAsync();
            if (player == null) throw new InvalidOperationException("No track is currently playing.");
            await player.SeekAsync(position, cancellationToken: _lifetimeToken);
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async Task SaveSnapshotAsync(string savedByUserId)
    {
        await _stopGate.WaitAsync(_lifetimeToken);
        try
        {
            ThrowIfRetired();
            lock (_stateLock)
            {
                if (_restoreInProgress) throw new InvalidOperationException("Cannot save during snapshot restore.");
            }
            await _playbackGate.WaitAsync(_lifetimeToken);
        }
        finally
        {
            _stopGate.Release();
        }

        try
        {
            // Snapshot queue + playback only after a handoff has either fully
            // published its current state or returned the track to the queue.
            await _queueManager.SaveSnapshotAsync(savedByUserId);
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public Task StopAsync() => StopCoreAsync(preserveCurrent: false);

    /// <summary>
    /// Permanently fences this engine against future playback starts, then
    /// drains and stops its current generation. Used before tenant eviction so
    /// a join that retained an old engine reference cannot restart it.
    /// </summary>
    public Task RetireAsync() => StopCoreAsync(preserveCurrent: false, retire: true);

    /// <summary>
    /// Stops the current playback generation and restores a saved snapshot.
    /// The current item is temporarily returned to the front of the live queue
    /// before restore begins. A successful restore replaces that queue; if the
    /// snapshot disappears or restore throws, the old item remains available
    /// and playback is restarted on the renewed generation.
    /// </summary>
    public async Task<PlaybackState?> RestoreSnapshotAsync()
    {
        await SetRestoreInProgressAsync(active: true);
        PlaybackState? restoredState;
        try
        {
            await StopCoreAsync(preserveCurrent: true);
            restoredState = await _queueManager.RestoreSnapshotAsync();
        }
        catch
        {
            await SetRestoreInProgressAsync(active: false);
            try
            {
                await MaybeStartAsync();
            }
            catch (Exception restartException)
            {
                // Preserve the restore failure for the command response while
                // retaining the old queue for a later manual /start-worm.
                _logger.LogError(restartException, "Failed to restart playback after snapshot restore rolled back.");
            }
            throw;
        }

        await SetRestoreInProgressAsync(active: false);
        if (restoredState is null)
        {
            await MaybeStartAsync();
        }

        return restoredState;
    }

    private async Task SetRestoreInProgressAsync(bool active)
    {
        await _stopGate.WaitAsync();
        try
        {
            lock (_stateLock)
            {
                if (active)
                {
                    if (_retired) throw new InvalidOperationException("A retired player cannot restore a snapshot.");
                    if (_restoreInProgress) throw new InvalidOperationException("A snapshot restore is already in progress.");
                }
                _restoreInProgress = active;
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task StopCoreAsync(bool preserveCurrent, bool retire = false)
    {
        await _stopGate.WaitAsync();
        try
        {
            CancellationTokenSource advanceCts;
            lock (_stateLock)
            {
                if (_retired) return;
                if (retire) _retired = true;
                advanceCts = _advanceCts;
            }
            try { advanceCts.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex, "Playback cancellation callback failed during stop; continuing teardown.");
            }

            // Drain any resolution/TTS start already holding the playback gate.
            // The generation remains canceled until stop completes, preventing
            // a queued wake-up from starting music in the stop window.
            await _playbackGate.WaitAsync();
            _playbackGate.Release();

            LavalinkPlayer? player = null;
            try
            {
                player = await TryGetPlayerAsync();
            }
            catch (Exception ex)
            {
                // A node outage must not bypass the authoritative local idle
                // cleanup below, especially during tenant retirement.
                _logger.LogWarning(ex, "Failed to retrieve Lavalink player while stopping; clearing local state.");
            }
            bool hasCurrentTrack;
            QueueItem? preservedCurrent = null;
            TaskCompletionSource? stopCompletion = null;
            lock (_stateLock)
            {
                hasCurrentTrack = _currentTrack != null;
                if (preserveCurrent) preservedCurrent = _currentTrack;
                if (hasCurrentTrack && player != null && _currentMusicIdentifier is not null)
                {
                    stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    AddStopExpectationLocked(new StopExpectation(
                        _playbackGeneration,
                        _currentMusicIdentifier,
                        StopIntent.Halt,
                        stopCompletion));
                }
            }

            try
            {
                if (player != null)
                {
                    await player.StopAsync(cancellationToken: _lifetimeToken);
                }

                if (stopCompletion != null)
                {
                    var completed = await Task.WhenAny(
                        stopCompletion.Task,
                        Task.Delay(TimeSpan.FromSeconds(2), _lifetimeToken));
                    if (completed != stopCompletion.Task)
                    {
                        _logger.LogWarning("Timed out waiting for Lavalink to acknowledge StopAsync.");
                    }
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    _currentTrack = null;
                    _currentMusicIdentifier = null;
                    _isPaused = false;
                    _ttsCompletion = null;
                    _currentTtsIdentifier = null;
                    RebuildStateLocked();
                }

                // Lavalink can lose the TrackEnded acknowledgement during a
                // disconnect; persist the idle fallback unconditionally.
                try
                {
                    await _queueRepository.UpdatePlaybackStateAsync(_guildIdStr, State);
                }
                catch (Exception ex)
                {
                    // Local stop/disconnect must complete even when SQLite is
                    // temporarily unavailable.
                    _logger.LogError(ex, "Failed to persist idle playback state while stopping.");
                }

                if (preservedCurrent is not null)
                {
                    // Suppress TrackQueued: restore either replaces this
                    // temporary rollback item or explicitly renews playback.
                    await _queueManager.RequeueFrontAsync(preservedCurrent, notify: false);
                }
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    /// <summary>
    /// Called by VoiceManager after the bot joins a voice channel. If a player
    /// is now available and nothing is playing, dequeue and start. No-op if
    /// there's no player yet (still connecting) or playback is already active.
    /// </summary>
    public async Task MaybeStartAsync()
    {
        // Pair generation renewal with playback-gate acquisition so a
        // concurrent StopAsync cannot observe a renewed token and then race a
        // new start into its stop window.
        await _stopGate.WaitAsync(_lifetimeToken);
        try
        {
            lock (_stateLock)
            {
                if (_retired || _restoreInProgress) return;
            }
            RenewAdvanceGeneration();
            await _playbackGate.WaitAsync(_lifetimeToken);
        }
        finally
        {
            _stopGate.Release();
        }

        try
        {
            var player = await TryGetPlayerAsync();
            if (player == null) return;
            bool hasCurrentTrack;
            lock (_stateLock) { hasCurrentTrack = _currentTrack != null; }
            if (hasCurrentTrack || player.State == PlayerState.Playing || player.State == PlayerState.Paused) return;
            await PlayNextCoreAsync(player);
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    private void RenewAdvanceGeneration()
    {
        CancellationTokenSource? retired = null;
        lock (_stateLock)
        {
            if (_advanceCts.IsCancellationRequested && !_lifetimeToken.IsCancellationRequested)
            {
                retired = _advanceCts;
                _advanceCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            }
        }
        retired?.Dispose();
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _queueManager.TrackQueued -= OnTrackQueued;
        _audioService.TrackStarted -= OnLavalinkTrackStartedAsync;
        _audioService.TrackEnded -= OnLavalinkTrackEndedAsync;
        lock (_stateLock)
        {
            _retired = true;
            foreach (var expectation in _stopExpectations)
            {
                expectation.Completion?.TrySetCanceled();
                expectation.Receipt?.TrySetCanceled();
            }
            _stopExpectations.Clear();
            _startExpectations.Clear();
        }
        try { _advanceCts.Cancel(); } catch (ObjectDisposedException) { }
        _lifetimeCts.Cancel();
        _transitions.Cancel();

        // The registry owns this instance but not the tasks already running
        // through it. Briefly quiesce the playback gate so cancellation can
        // return a dequeued-but-not-started track before the queue registry and
        // StateStore are disposed later in the DI teardown order.
        bool quiesced = false;
        try
        {
            quiesced = _playbackGate.Wait(TimeSpan.FromSeconds(2));
            if (quiesced) _playbackGate.Release();
        }
        catch (ObjectDisposedException) { }

        if (quiesced) _lifetimeCts.Dispose();
    }
}
