using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Earworm.Config;
using Earworm.Persistence.Repositories;
using Earworm.Domain.Player;

namespace Earworm.Domain.Queue;

public class QueueManager : IDisposable
{
    private readonly IQueueRepository _queueRepository;
    private readonly ISnapshotRepository _snapshotRepository;
    private readonly EarwormConfig _config;
    private readonly ILogger<QueueManager> _logger;
    private readonly string _guildId;

    private readonly List<QueueItem> _queue = new();
    private readonly object _lock = new();

    // Lazy-hydration gate. Only the startup loop calls InitializeAsync; engines
    // created lazily (runtime-admitted / re-admitted guilds, or a guild whose
    // startup hydration threw and was swallowed) never go through it. Every
    // mutating op funnels through EnsureInitializedAsync first so the in-memory
    // queue always reflects the persisted rows before positions are computed.
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    // Event definitions. Virtual so tests using NSubstitute can Raise.Event them
    // — see PlayerEngine_WakesUp_WhenTrackQueuedAfterEmptyQueue.
    public virtual event Action<QueueItem>? TrackQueued;
    public virtual event Action<QueueItem>? TrackRemoved;
    public virtual event Action? QueueCleared;
    public virtual event Action? SnapshotSaved;
    public virtual event Action? SnapshotRestored;

    public QueueManager(
        IQueueRepository queueRepository,
        ISnapshotRepository snapshotRepository,
        EarwormConfig config,
        ILogger<QueueManager> logger,
        string guildId)
    {
        _queueRepository = queueRepository;
        _snapshotRepository = snapshotRepository;
        _config = config;
        _logger = logger;
        _guildId = guildId;
    }

    /// <summary>The Discord guild this queue belongs to.</summary>
    public string GuildId => _guildId;

    /// <summary>
    /// Loads the persisted queue from SQLite into memory. Called once at startup
    /// after schema migrations; safe to call again to force a re-sync.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _initGate.WaitAsync();
        try
        {
            await LoadFromDatabaseLockedAsync();
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Loads the persisted queue exactly once on first access. A guild whose
    /// QueueManager was created outside the startup loop (runtime <c>add-server</c>,
    /// re-admit after suspend, or a startup hydration that failed) would otherwise
    /// start with an empty in-memory queue — hiding its persisted rows and making
    /// the next enqueue compute position 0, colliding with the existing row on
    /// UNIQUE(guild_id, position). Cheap no-op once hydrated.
    /// </summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initGate.WaitAsync();
        try
        {
            if (_initialized) return; // another caller hydrated while we waited
            await LoadFromDatabaseLockedAsync();
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>Reload from the DB. Caller must hold <see cref="_initGate"/>.</summary>
    private async Task LoadFromDatabaseLockedAsync()
    {
        var dbQueue = await _queueRepository.GetQueueAsync(_guildId);
        lock (_lock)
        {
            _queue.Clear();
            _queue.AddRange(dbQueue);
        }
        _initialized = true;
        _logger.LogInformation("QueueManager[{GuildId}] loaded {Count} tracks from database.", _guildId, _queue.Count);
    }

    /// <summary>
    /// Gets a read-only list of the current queue.
    /// </summary>
    public virtual List<QueueItem> GetQueue()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }

    /// <summary>
    /// Cheap count accessor that avoids the per-call full-queue copy from <see cref="GetQueue"/>.
    /// </summary>
    public virtual int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    /// Adds a track to the queue, checking all configured constraints.
    /// Returns the finalized <see cref="QueueItem"/> as it was added to the in-memory queue
    /// (including the database-assigned <see cref="QueueItem.QueueItemId"/> and <see cref="QueueItem.Position"/>).
    /// </summary>
    public async Task<QueueItem> AddTrackAsync(
        string sourceType,
        string sourceId,
        string title,
        string artist,
        int? durationSeconds,
        string requestedByUserId,
        string requestedByDisplayName,
        string guildId)
    {
        await EnsureInitializedAsync();

        // The row belongs to THIS manager's guild, never the caller-supplied
        // argument. Positions are computed against our own in-memory queue, so a
        // mismatched guild_id would write into another tenant at a position that
        // can collide on UNIQUE(guild_id, position). Flag a mismatch defensively.
        if (!string.Equals(guildId, _guildId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "AddTrackAsync received guildId '{Arg}' on the QueueManager for '{Owner}'; using the owner guild.",
                guildId, _guildId);
        }

        var correlationId = Guid.NewGuid();
        var item = new QueueItem
        {
            SourceType = sourceType,
            SourceId = sourceId,
            Title = title,
            Artist = artist,
            DurationSeconds = durationSeconds,
            RequestedByUserId = requestedByUserId,
            RequestedByDisplayName = requestedByDisplayName,
            QueuedAt = DateTimeOffset.UtcNow,
            GuildId = _guildId,
            CorrelationId = correlationId
        };

        Task<long> writeTask;
        QueueItem finalItem;
        Action<QueueItem>? handler;
        int pos;

        lock (_lock)
        {
            EnforceQueueCapsLocked(durationSeconds, requestedByUserId);

            pos = _queue.Count;
            // SubmitWriteAsync only enqueues to an unbounded channel (no I/O),
            // so we can safely call it under the lock and capture the Task. The DB
            // assigns its own gap-free position; pos here is the dense in-memory one.
            writeTask = _queueRepository.AddTrackAsync(item);

            finalItem = item with { Position = pos };
            _queue.Add(finalItem);

            handler = TrackQueued;
        }

        long newId;
        try
        {
            newId = await writeTask;
        }
        catch
        {
            // Best-effort rollback if persistence fails after in-memory append.
            // Uses CorrelationId (a Guid) instead of SourceId + QueuedAt so two
            // identical tracks queued in the same tick don't match each other.
            lock (_lock)
            {
                int idx = _queue.FindIndex(q => q.QueueItemId == 0 && q.CorrelationId == correlationId);
                if (idx >= 0)
                {
                    _queue.RemoveAt(idx);
                    for (int i = idx; i < _queue.Count; i++)
                    {
                        _queue[i] = _queue[i] with { Position = i };
                    }
                }
            }
            throw;
        }

        QueueItem itemForEvent;
        bool orphaned;
        lock (_lock)
        {
            int idx = _queue.FindIndex(q => q.QueueItemId == 0 && q.CorrelationId == correlationId);
            if (idx >= 0)
            {
                _queue[idx] = _queue[idx] with { QueueItemId = newId };
                itemForEvent = _queue[idx];
                orphaned = false;
            }
            else
            {
                // In-memory item was removed (e.g. Dequeue/Clear) before we could
                // backfill the row id; the row in the DB is now an orphan.
                itemForEvent = finalItem with { QueueItemId = newId };
                orphaned = true;
            }
        }

        if (orphaned)
        {
            try
            {
                await _queueRepository.RemoveTrackAsync(newId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up orphaned queue row {Id} after in-memory removal.", newId);
            }
            return itemForEvent;
        }

        _logger.LogInformation("Queued track: {Title} by {Artist} at position {Pos}", title, artist, pos);
        handler?.Invoke(itemForEvent);
        return itemForEvent;
    }

    /// <summary>
    /// Enforces queue limit constraints. Must be called while holding <see cref="_lock"/>.
    /// </summary>
    private void EnforceQueueCapsLocked(int? durationSeconds, string userId)
    {
        // 1. Total Queue Length Cap
        if (_config.Queue.LengthCap.HasValue && _queue.Count >= _config.Queue.LengthCap.Value)
        {
            throw new InvalidOperationException($"The queue is full (limit: {_config.Queue.LengthCap.Value} tracks).");
        }

        // 2. Per-Track Length Cap
        if (_config.Queue.PerTrackLengthCapSeconds.HasValue && durationSeconds.HasValue &&
            durationSeconds.Value > _config.Queue.PerTrackLengthCapSeconds.Value)
        {
            var capMinutes = TimeSpan.FromSeconds(_config.Queue.PerTrackLengthCapSeconds.Value).TotalMinutes;
            throw new InvalidOperationException($"This track is too long (limit: {capMinutes:F1} minutes).");
        }

        // 3. Per-Requester Contiguous Cap (consecutive back-to-back)
        if (_config.Queue.PerRequesterContiguousCap.HasValue)
        {
            int contiguous = 0;
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (_queue[i].RequestedByUserId == userId)
                {
                    contiguous++;
                }
                else
                {
                    break;
                }
            }

            if (contiguous >= _config.Queue.PerRequesterContiguousCap.Value)
            {
                throw new InvalidOperationException($"You already have {_config.Queue.PerRequesterContiguousCap.Value} consecutive tracks at the end of the queue. Wait for others to queue or for your tracks to play.");
            }
        }
    }

    /// <summary>
    /// Removes a track at a specific 0-based index.
    /// </summary>
    public async Task<QueueItem> RemoveTrackAsync(int position, string userId, bool isDj)
    {
        await EnsureInitializedAsync();

        QueueItem itemToRemove;
        Task writeTask;
        Action<QueueItem>? handler;

        lock (_lock)
        {
            if (position < 0 || position >= _queue.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(position), "Queue position is out of bounds.");
            }

            itemToRemove = _queue[position];

            // Enforce permission checks: DJ or the original requester
            if (!isDj && itemToRemove.RequestedByUserId != userId)
            {
                throw new InvalidOperationException("You can only remove tracks that you queued yourself. (Requires DJ role to remove others' tracks).");
            }

            writeTask = _queueRepository.RemoveTrackAsync(itemToRemove.QueueItemId);

            _queue.RemoveAt(position);
            for (int i = position; i < _queue.Count; i++)
            {
                _queue[i] = _queue[i] with { Position = i };
            }

            handler = TrackRemoved;
        }

        await writeTask;

        _logger.LogInformation("Removed track at position {Pos}: {Title}", position, itemToRemove.Title);
        handler?.Invoke(itemToRemove);

        return itemToRemove;
    }

    /// <summary>
    /// Moves a track from one position to another (DJ-only).
    /// </summary>
    public async Task MoveTrackAsync(int fromPosition, int toPosition)
    {
        await EnsureInitializedAsync();

        Task writeTask;
        lock (_lock)
        {
            if (fromPosition < 0 || fromPosition >= _queue.Count || toPosition < 0 || toPosition >= _queue.Count)
            {
                throw new ArgumentOutOfRangeException("Queue position is out of bounds.");
            }

            if (fromPosition == toPosition)
            {
                return;
            }

            var item = _queue[fromPosition];
            writeTask = _queueRepository.MoveTrackAsync(_guildId, item.QueueItemId, toPosition);

            _queue.RemoveAt(fromPosition);
            _queue.Insert(toPosition, item);

            // Re-index all positions
            for (int i = 0; i < _queue.Count; i++)
            {
                _queue[i] = _queue[i] with { Position = i };
            }

            _logger.LogInformation("Moved track from {FromPos} to {ToPos}: {Title}", fromPosition, toPosition, item.Title);
        }

        await writeTask;
    }

    /// <summary>
    /// Clears all tracks from the queue.
    /// </summary>
    public async Task ClearQueueAsync()
    {
        Task writeTask;
        Action? handler;
        lock (_lock)
        {
            writeTask = _queueRepository.ClearQueueAsync(_guildId);
            _queue.Clear();
            handler = QueueCleared;
        }

        await writeTask;

        _logger.LogInformation("Queue cleared.");
        handler?.Invoke();
    }

    /// <summary>
    /// Pulls the first track from the front of the queue (pops it).
    /// </summary>
    public virtual async Task<QueueItem?> DequeueAsync()
    {
        await EnsureInitializedAsync();

        QueueItem head;
        Task writeTask;
        Action<QueueItem>? handler;

        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                return null;
            }

            head = _queue[0];
            writeTask = _queueRepository.RemoveTrackAsync(head.QueueItemId);

            _queue.RemoveAt(0);
            for (int i = 0; i < _queue.Count; i++)
            {
                _queue[i] = _queue[i] with { Position = i };
            }

            handler = TrackRemoved;
        }

        await writeTask;

        _logger.LogInformation("Dequeued track: {Title}", head.Title);
        handler?.Invoke(head);

        return head;
    }

    /// <summary>
    /// Re-queues a track at the front of the queue (position 0). Bypasses
    /// queue caps — PRD §7 frames /previous as an unconditional DJ rewind,
    /// so it shouldn't be rejected just because the queue happens to be full.
    /// </summary>
    public virtual async Task RequeueFrontAsync(QueueItem item)
    {
        await EnsureInitializedAsync();

        var correlationId = Guid.NewGuid();
        var fresh = new QueueItem
        {
            SourceType = item.SourceType,
            SourceId = item.SourceId,
            Title = item.Title,
            Artist = item.Artist,
            DurationSeconds = item.DurationSeconds,
            RequestedByUserId = item.RequestedByUserId,
            RequestedByDisplayName = item.RequestedByDisplayName,
            QueuedAt = DateTimeOffset.UtcNow,
            GuildId = _guildId,
            CorrelationId = correlationId
        };

        Task<long> addTask;
        int lastPos;
        lock (_lock)
        {
            lastPos = _queue.Count;
            addTask = _queueRepository.AddTrackAsync(fresh);
            _queue.Add(fresh with { Position = lastPos });
        }

        long newId = await addTask;

        // Backfill the row id and move it to the front atomically under one lock.
        // The position captured before the await (lastPos) can be stale — a
        // concurrent Dequeue/Remove may have shifted the queue while addTask was in
        // flight — so locate the row by id now and move by id, never by the stale
        // position (which could be out of bounds or point at a different track).
        Task moveTask = Task.CompletedTask;
        QueueItem finalItem;
        Action<QueueItem>? handler;
        lock (_lock)
        {
            int idx = _queue.FindIndex(q => q.QueueItemId == 0 && q.CorrelationId == correlationId);
            if (idx >= 0)
            {
                _queue[idx] = _queue[idx] with { QueueItemId = newId };
            }

            if (idx > 0)
            {
                var moving = _queue[idx];
                _queue.RemoveAt(idx);
                _queue.Insert(0, moving);
                for (int i = 0; i < _queue.Count; i++)
                {
                    _queue[i] = _queue[i] with { Position = i };
                }
                // Repo write only enqueues to the channel, safe under the lock.
                moveTask = _queueRepository.MoveTrackAsync(_guildId, newId, 0);
            }

            int finalIdx = _queue.FindIndex(q => q.QueueItemId == newId);
            finalItem = finalIdx >= 0
                ? _queue[finalIdx]
                : fresh with { QueueItemId = newId, Position = 0 };
            handler = TrackQueued;
        }

        await moveTask;

        handler?.Invoke(finalItem);
    }

    /// <summary>
    /// Captures a snapshot of the current queue and saves it.
    /// </summary>
    public async Task SaveSnapshotAsync(string savedByUserId)
    {
        await _snapshotRepository.SaveSnapshotAsync(_guildId, savedByUserId);
        _logger.LogInformation("Saved snapshot of queue for user {UserId}.", savedByUserId);
        SnapshotSaved?.Invoke();
    }

    /// <summary>
    /// Restores a previously saved snapshot.
    /// Returns the restored playback state.
    /// </summary>
    public async Task<PlaybackState?> RestoreSnapshotAsync()
    {
        var restored = await _snapshotRepository.RestoreSnapshotAsync(_guildId);
        if (restored == null)
        {
            return null;
        }

        Action? handler;
        int restoredCount;
        lock (_lock)
        {
            _queue.Clear();
            _queue.AddRange(restored.Value.QueueItems);
            restoredCount = _queue.Count;
            handler = SnapshotRestored;
        }

        _logger.LogInformation("Restored snapshot with {Count} tracks.", restoredCount);
        handler?.Invoke();

        return restored.Value.PlaybackState;
    }

    /// <summary>Disposes the hydration gate. Called when the guild is evicted.</summary>
    public void Dispose()
    {
        _initGate.Dispose();
    }
}
