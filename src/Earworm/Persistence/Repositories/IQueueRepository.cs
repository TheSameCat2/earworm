using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public interface IQueueRepository
{
    Task<List<QueueItem>> GetQueueAsync();
    Task AddTrackAsync(QueueItem item);
    Task RemoveTrackAsync(int position);
    Task MoveTrackAsync(int fromPosition, int toPosition);
    Task ClearQueueAsync();
    
    Task<PlaybackState> GetPlaybackStateAsync();
    Task UpdatePlaybackStateAsync(PlaybackState state);
    Task UpdatePlaybackPositionAsync(int positionMs);
}
