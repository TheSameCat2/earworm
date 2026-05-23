using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public interface IQueueRepository
{
    Task<List<QueueItem>> GetQueueAsync();
    Task<long> AddTrackAsync(QueueItem item);
    Task RemoveTrackAsync(long queueItemId);
    Task MoveTrackAsync(long queueItemId, int toPosition);
    Task ClearQueueAsync();

    Task<PlaybackState> GetPlaybackStateAsync();
    Task UpdatePlaybackStateAsync(PlaybackState state);
    Task UpdatePlaybackPositionAsync(int positionMs);
}
