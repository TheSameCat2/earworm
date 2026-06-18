using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public interface IQueueRepository
{
    Task<List<QueueItem>> GetQueueAsync(string guildId);
    Task<long> AddTrackAsync(QueueItem item, int position);
    Task CompactPositionsAsync(string guildId);
    Task RemoveTrackAsync(long queueItemId);
    Task MoveTrackAsync(string guildId, long queueItemId, int toPosition);
    Task ClearQueueAsync(string guildId);

    Task<PlaybackState> GetPlaybackStateAsync(string guildId);
    Task UpdatePlaybackStateAsync(string guildId, PlaybackState state);
    Task UpdatePlaybackPositionAsync(string guildId, int positionMs);
}
