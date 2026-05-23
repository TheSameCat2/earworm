using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Telemetry;

namespace Earworm.Persistence.Repositories;

public interface IMetricsRepository
{
    /// <summary>
    /// Increments a global metric counter by the given amount (default 1).
    /// </summary>
    Task IncrementGlobalMetricAsync(string key, long amount = 1);

    /// <summary>
    /// Retrieves a single global metric value, returning 0 if it doesn't exist.
    /// </summary>
    Task<long> GetGlobalMetricAsync(string key);

    /// <summary>
    /// Retrieves all global metrics as a key-value dictionary.
    /// </summary>
    Task<Dictionary<string, long>> GetGlobalMetricsAsync();

    /// <summary>
    /// Increments a specific column counter for a user.
    /// Valid whitelisted columns: tracks_queued, tracks_completed, listening_seconds, requests_youtube, requests_soundcloud, requests_mp3_upload, requests_search
    /// </summary>
    Task IncrementUserMetricAsync(string userId, string displayName, string column, long amount = 1);

    /// <summary>
    /// Retrieves a user's metric counters, or null if not found.
    /// </summary>
    Task<UserMetrics?> GetUserMetricsAsync(string userId);

    /// <summary>
    /// Retrieves the top users by total listening seconds.
    /// </summary>
    Task<List<UserMetrics>> GetTopUsersByListeningTimeAsync(int limit);

    /// <summary>
    /// Retrieves the top users by total tracks queued.
    /// </summary>
    Task<List<UserMetrics>> GetTopUsersByTracksQueuedAsync(int limit);
}
