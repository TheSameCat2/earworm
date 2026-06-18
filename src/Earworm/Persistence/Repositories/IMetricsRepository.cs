using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Domain.Telemetry;

namespace Earworm.Persistence.Repositories;

/// <summary>
/// Describes a single metric counter increment for use with <see cref="IMetricsRepository.IncrementBatchAsync"/>.
/// When <see cref="UserId"/> is null the increment is applied to the global metrics table;
/// otherwise it is applied to the per-user table for the given user.
/// </summary>
public record MetricIncrement(
    string Column,
    long Amount,
    string? UserId = null,
    string? DisplayName = null);

public interface IMetricsRepository
{
    /// <summary>
    /// Increments a global metric counter for the guild by the given amount (default 1).
    /// </summary>
    Task IncrementGlobalMetricAsync(string guildId, string key, long amount = 1);

    /// <summary>
    /// Retrieves a single global metric value for the guild, returning 0 if it doesn't exist.
    /// </summary>
    Task<long> GetGlobalMetricAsync(string guildId, string key);

    /// <summary>
    /// Retrieves all of the guild's global metrics as a key-value dictionary.
    /// </summary>
    Task<Dictionary<string, long>> GetGlobalMetricsAsync(string guildId);

    /// <summary>
    /// Increments a specific column counter for a user within the guild.
    /// Valid whitelisted columns: tracks_queued, tracks_completed, listening_seconds, requests_youtube, requests_soundcloud, requests_mp3_upload, requests_search
    /// </summary>
    Task IncrementUserMetricAsync(string guildId, string userId, string displayName, string column, long amount = 1);

    /// <summary>
    /// Atomically increments all supplied metric counters for the guild inside a single
    /// write-channel round-trip and a single SQLite transaction.
    /// Increments with a null <see cref="MetricIncrement.UserId"/> are applied to the global table;
    /// those with a non-null UserId are applied to the per-user table.
    /// </summary>
    Task IncrementBatchAsync(string guildId, IReadOnlyCollection<MetricIncrement> increments);

    /// <summary>
    /// Retrieves a user's metric counters within the guild, or null if not found.
    /// </summary>
    Task<UserMetrics?> GetUserMetricsAsync(string guildId, string userId);

    /// <summary>
    /// Retrieves the guild's top users by total listening seconds.
    /// </summary>
    Task<List<UserMetrics>> GetTopUsersByListeningTimeAsync(string guildId, int limit);

    /// <summary>
    /// Retrieves the guild's top users by total tracks queued.
    /// </summary>
    Task<List<UserMetrics>> GetTopUsersByTracksQueuedAsync(string guildId, int limit);
}
