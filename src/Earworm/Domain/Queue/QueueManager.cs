using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Earworm.Config;
using Earworm.Persistence.Repositories;
using Earworm.Domain.Player;

namespace Earworm.Domain.Queue;

public class QueueManager
{
    private readonly IQueueRepository _queueRepository;
    private readonly ISnapshotRepository _snapshotRepository;
    private readonly EarwormConfig _config;
    private readonly ILogger<QueueManager> _logger;
    
    private readonly List<QueueItem> _queue = new();
    private readonly object _lock = new();

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
        ILogger<QueueManager> logger)
    {
        _queueRepository = queueRepository;
        _snapshotRepository = snapshotRepository;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Loads the persisted queue from SQLite into memory. Call this once at
    /// startup after schema migrations have completed. Safe to call again to
    /// re-sync from the database.
    /// </summary>
    public async Task InitializeAsync()
    {
        var dbQueue = await _queueRepository.GetQueueAsync();
        lock (_lock)
        {
            _queue.Clear();
            _queue.AddRange(dbQueue);
            _logger.LogInformation("QueueManager initialized with {Count} tracks from database.", _queue.Count);
        }
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
    /// Adds a track to the queue, checking all configured constraints.
    /// </summary>
    public async Task AddTrackAsync(
        string sourceType,
        string sourceId,
        string title,
        string artist,
        int? durationSeconds,
        string requestedByUserId,
        string requestedByDisplayName,
        string guildId)
    {
        // Enforce caps
        EnforceQueueCaps(durationSeconds, requestedByUserId);

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
            GuildId = guildId
        };

        // Persist to SQLite
        await _queueRepository.AddTrackAsync(item);

        // Update in-memory state
        lock (_lock)
        {
            // Position is assigned by the repository (max position + 1)
            int pos = _queue.Count;
            var finalItem = item with { Position = pos };
            _queue.Add(finalItem);
            
            _logger.LogInformation("Queued track: {Title} by {Artist} at position {Pos}", title, artist, pos);
            TrackQueued?.Invoke(finalItem);
        }
    }

    /// <summary>
    /// Enforces queue limit constraints (throws InvalidOperationException on failure).
    /// </summary>
    private void EnforceQueueCaps(int? durationSeconds, string userId)
    {
        lock (_lock)
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
    }

    /// <summary>
    /// Removes a track at a specific 0-based index.
    /// </summary>
    public async Task<QueueItem> RemoveTrackAsync(int position, string userId, bool isDj)
    {
        QueueItem itemToRemove;
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
        }

        // Persist to SQLite
        await _queueRepository.RemoveTrackAsync(position);

        // Update in-memory state
        lock (_lock)
        {
            _queue.RemoveAt(position);
            // Shift subsequent positions in-memory
            for (int i = position; i < _queue.Count; i++)
            {
                _queue[i] = _queue[i] with { Position = i };
            }
            
            _logger.LogInformation("Removed track at position {Pos}: {Title}", position, itemToRemove.Title);
            TrackRemoved?.Invoke(itemToRemove);
        }

        return itemToRemove;
    }

    /// <summary>
    /// Moves a track from one position to another (DJ-only).
    /// </summary>
    public async Task MoveTrackAsync(int fromPosition, int toPosition)
    {
        lock (_lock)
        {
            if (fromPosition < 0 || fromPosition >= _queue.Count || toPosition < 0 || toPosition >= _queue.Count)
            {
                throw new ArgumentOutOfRangeException("Queue position is out of bounds.");
            }
        }

        // Persist to SQLite
        await _queueRepository.MoveTrackAsync(fromPosition, toPosition);

        // Update in-memory state
        lock (_lock)
        {
            var item = _queue[fromPosition];
            _queue.RemoveAt(fromPosition);
            _queue.Insert(toPosition, item);

            // Re-index all positions
            for (int i = 0; i < _queue.Count; i++)
            {
                _queue[i] = _queue[i] with { Position = i };
            }
            
            _logger.LogInformation("Moved track from {FromPos} to {ToPos}: {Title}", fromPosition, toPosition, item.Title);
        }
    }

    /// <summary>
    /// Clears all tracks from the queue.
    /// </summary>
    public async Task ClearQueueAsync()
    {
        // Persist to SQLite
        await _queueRepository.ClearQueueAsync();

        lock (_lock)
        {
            _queue.Clear();
            _logger.LogInformation("Queue cleared.");
            QueueCleared?.Invoke();
        }
    }

    /// <summary>
    /// Pulls the first track from the front of the queue (pops it).
    /// </summary>
    public virtual async Task<QueueItem?> DequeueAsync()
    {
        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                return null;
            }
        }

        // We remove it from position 0 in the DB (which auto-shifts everything else down)
        // Note: isDj=true bypasses permission check
        return await RemoveTrackAsync(0, string.Empty, isDj: true);
    }

    /// <summary>
    /// Re-queues a track at the front of the queue (position 0). Bypasses
    /// queue caps — PRD §7 frames /previous as an unconditional DJ rewind,
    /// so it shouldn't be rejected just because the queue happens to be full.
    /// </summary>
    public virtual async Task RequeueFrontAsync(QueueItem item)
    {
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
            GuildId = item.GuildId
        };

        await _queueRepository.AddTrackAsync(fresh);

        int lastPos;
        lock (_lock)
        {
            lastPos = _queue.Count;
            _queue.Add(fresh with { Position = lastPos });
        }

        if (lastPos > 0)
        {
            await MoveTrackAsync(lastPos, 0);
        }

        // Make sure listeners (the playback waker, etc.) still see the
        // re-queue as a normal TrackQueued event.
        TrackQueued?.Invoke(fresh with { Position = 0 });
    }

    /// <summary>
    /// Captures a snapshot of the current queue and saves it.
    /// </summary>
    public async Task SaveSnapshotAsync(string savedByUserId)
    {
        await _snapshotRepository.SaveSnapshotAsync(savedByUserId);
        _logger.LogInformation("Saved snapshot of queue for user {UserId}.", savedByUserId);
        SnapshotSaved?.Invoke();
    }

    /// <summary>
    /// Restores a previously saved snapshot.
    /// Returns the restored playback state.
    /// </summary>
    public async Task<PlaybackState?> RestoreSnapshotAsync()
    {
        var restored = await _snapshotRepository.RestoreSnapshotAsync();
        if (restored == null)
        {
            return null;
        }

        lock (_lock)
        {
            _queue.Clear();
            _queue.AddRange(restored.Value.QueueItems);
            
            _logger.LogInformation("Restored snapshot with {Count} tracks.", _queue.Count);
            SnapshotRestored?.Invoke();
        }

        return restored.Value.PlaybackState;
    }
}
