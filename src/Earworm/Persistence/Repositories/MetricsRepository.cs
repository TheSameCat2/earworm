using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Earworm.Domain.Telemetry;

namespace Earworm.Persistence.Repositories;

public sealed class MetricsRepository : IMetricsRepository
{
    private static readonly HashSet<string> WhitelistedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "tracks_queued",
        "tracks_completed",
        "listening_seconds",
        "requests_youtube",
        "requests_soundcloud",
        "requests_mp3_upload",
        "requests_search"
    };

    private readonly StateStore _stateStore;

    public MetricsRepository(StateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task IncrementGlobalMetricAsync(string key, long amount = 1)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO metrics_global (metric_key, metric_value, updated_at)
                VALUES ($key, $amount, $updatedAt)
                ON CONFLICT(metric_key) DO UPDATE SET 
                    metric_value = metric_value + excluded.metric_value,
                    updated_at = excluded.updated_at;
            ";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$amount", amount);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<long> GetGlobalMetricAsync(string key)
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT metric_value FROM metrics_global WHERE metric_key = $key;";
        cmd.Parameters.AddWithValue("$key", key);

        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? Convert.ToInt64(result) : 0;
    }

    public async Task<Dictionary<string, long>> GetGlobalMetricsAsync()
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT metric_key, metric_value FROM metrics_global;";

        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            dict[reader.GetString(0)] = reader.GetInt64(1);
        }
        return dict;
    }

    public async Task IncrementUserMetricAsync(string userId, string displayName, string column, long amount = 1)
    {
        if (!WhitelistedColumns.Contains(column))
        {
            throw new ArgumentException($"Invalid user metric column: {column}", nameof(column));
        }

        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            // Since column is strictly whitelisted above, we can safely interpolate it.
            cmd.CommandText = $@"
                INSERT INTO metrics_per_user (user_id, display_name_last_seen, {column}, updated_at)
                VALUES ($userId, $displayName, $amount, $updatedAt)
                ON CONFLICT(user_id) DO UPDATE SET 
                    display_name_last_seen = excluded.display_name_last_seen,
                    {column} = {column} + excluded.{column},
                    updated_at = excluded.updated_at;
            ";
            cmd.Parameters.AddWithValue("$userId", userId);
            cmd.Parameters.AddWithValue("$displayName", displayName);
            cmd.Parameters.AddWithValue("$amount", amount);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<UserMetrics?> GetUserMetricsAsync(string userId)
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, display_name_last_seen, tracks_queued, tracks_completed, listening_seconds, 
                   requests_youtube, requests_soundcloud, requests_mp3_upload, requests_search, updated_at
            FROM metrics_per_user
            WHERE user_id = $userId;
        ";
        cmd.Parameters.AddWithValue("$userId", userId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapUserMetrics(reader);
        }
        return null;
    }

    public async Task<List<UserMetrics>> GetTopUsersByListeningTimeAsync(int limit)
    {
        return await GetTopUsersInternalAsync("listening_seconds", limit);
    }

    public async Task<List<UserMetrics>> GetTopUsersByTracksQueuedAsync(int limit)
    {
        return await GetTopUsersInternalAsync("tracks_queued", limit);
    }

    private async Task<List<UserMetrics>> GetTopUsersInternalAsync(string orderByColumn, int limit)
    {
        // orderByColumn is interpolated into SQL below; whitelist defensively in
        // case a future caller passes a user-controlled string.
        if (!WhitelistedColumns.Contains(orderByColumn))
        {
            throw new ArgumentException($"Invalid order-by column: {orderByColumn}", nameof(orderByColumn));
        }

        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT user_id, display_name_last_seen, tracks_queued, tracks_completed, listening_seconds, 
                   requests_youtube, requests_soundcloud, requests_mp3_upload, requests_search, updated_at
            FROM metrics_per_user
            ORDER BY {orderByColumn} DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<UserMetrics>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapUserMetrics(reader));
        }
        return list;
    }

    private static UserMetrics MapUserMetrics(SqliteDataReader reader)
    {
        return new UserMetrics
        {
            UserId = reader.GetString(0),
            DisplayNameLastSeen = reader.GetString(1),
            TracksQueued = reader.GetInt64(2),
            TracksCompleted = reader.GetInt64(3),
            ListeningSeconds = reader.GetInt64(4),
            RequestsYoutube = reader.GetInt64(5),
            RequestsSoundcloud = reader.GetInt64(6),
            RequestsMp3Upload = reader.GetInt64(7),
            RequestsSearch = reader.GetInt64(8),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9))
        };
    }
}
