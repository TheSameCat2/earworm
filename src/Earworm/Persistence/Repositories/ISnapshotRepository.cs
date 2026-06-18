using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public interface ISnapshotRepository
{
    /// <summary>
    /// Saves the guild's current playback state and live queue as its snapshot.
    /// </summary>
    Task SaveSnapshotAsync(string guildId, string savedByUserId);

    /// <summary>
    /// Checks if a valid snapshot exists for the guild.
    /// </summary>
    Task<bool> HasSnapshotAsync(string guildId);

    /// <summary>
    /// Restores the guild's saved snapshot back into its active queue and playback state.
    /// Returns the restored state, or null if no snapshot exists.
    /// </summary>
    Task<(PlaybackState PlaybackState, List<QueueItem> QueueItems)?> RestoreSnapshotAsync(string guildId);

    /// <summary>
    /// Clears the guild's saved snapshot, setting saved_at to null and emptying its snapshot queue.
    /// </summary>
    Task ClearSnapshotAsync(string guildId);
}
