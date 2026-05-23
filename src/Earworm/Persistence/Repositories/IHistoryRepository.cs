using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public interface IHistoryRepository
{
    /// <summary>
    /// Adds a play history entry and prunes excess entries exceeding retentionCount.
    /// Insertion and pruning happen atomically inside a single transaction.
    /// </summary>
    Task AddHistoryEntryAsync(PlayHistoryEntry entry, int retentionCount);

    /// <summary>
    /// Retrieves the most recent N history entries, ordered by played_at DESC.
    /// </summary>
    Task<List<PlayHistoryEntry>> GetRecentHistoryAsync(int limit);
}
