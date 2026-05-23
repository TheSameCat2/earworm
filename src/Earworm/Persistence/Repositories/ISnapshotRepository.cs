using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public interface ISnapshotRepository
{
    /// <summary>
    /// Saves the current playback state and live queue as a snapshot.
    /// </summary>
    Task SaveSnapshotAsync(string savedByUserId);

    /// <summary>
    /// Checks if a valid snapshot exists to be restored.
    /// </summary>
    Task<bool> HasSnapshotAsync();

    /// <summary>
    /// Restores the saved snapshot back into the active queue and playback state tables.
    /// Returns the restored state, or null if no snapshot exists.
    /// </summary>
    Task<(PlaybackState PlaybackState, List<QueueItem> QueueItems)?> RestoreSnapshotAsync();

    /// <summary>
    /// Clears the saved snapshot, setting saved_at to null and emptying the snapshot queue.
    /// </summary>
    Task ClearSnapshotAsync();
}
