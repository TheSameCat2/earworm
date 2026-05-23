using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public sealed class HistoryRepository : IHistoryRepository
{
    private readonly StateStore _stateStore;

    public HistoryRepository(StateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task AddHistoryEntryAsync(PlayHistoryEntry entry, int retentionCount)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                // 1. Insert history entry
                using (var insCmd = connection.CreateCommand())
                {
                    insCmd.Transaction = transaction;
                    insCmd.CommandText = @"
                        INSERT INTO history (played_at, source_type, source_id, title, artist, duration_seconds, 
                                             played_seconds, requested_by_user_id, requested_by_display_name, skipped, failed, failure_reason, guild_id)
                        VALUES ($playedAt, $sourceType, $sourceId, $title, $artist, $duration, 
                                $playedSeconds, $userId, $displayName, $skipped, $failed, $failureReason, $guildId);
                    ";
                    insCmd.Parameters.AddWithValue("$playedAt", entry.PlayedAt.ToUnixTimeMilliseconds());
                    insCmd.Parameters.AddWithValue("$sourceType", entry.SourceType);
                    insCmd.Parameters.AddWithValue("$sourceId", entry.SourceId);
                    insCmd.Parameters.AddWithValue("$title", (object?)entry.Title ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$artist", (object?)entry.Artist ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$duration", (object?)entry.DurationSeconds ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$playedSeconds", (object?)entry.PlayedSeconds ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$userId", entry.RequestedByUserId);
                    insCmd.Parameters.AddWithValue("$displayName", entry.RequestedByDisplayName);
                    insCmd.Parameters.AddWithValue("$skipped", entry.Skipped ? 1 : 0);
                    insCmd.Parameters.AddWithValue("$failed", entry.Failed ? 1 : 0);
                    insCmd.Parameters.AddWithValue("$failureReason", (object?)entry.FailureReason ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$guildId", entry.GuildId);

                    await insCmd.ExecuteNonQueryAsync();
                }

                // 2. Prune excess entries
                using (var pruneCmd = connection.CreateCommand())
                {
                    pruneCmd.Transaction = transaction;
                    pruneCmd.CommandText = @"
                        DELETE FROM history 
                        WHERE history_id NOT IN (
                            SELECT history_id 
                            FROM history 
                            ORDER BY played_at DESC 
                            LIMIT $retentionCount
                        );
                    ";
                    pruneCmd.Parameters.AddWithValue("$retentionCount", retentionCount);
                    await pruneCmd.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        });
    }

    public async Task<List<PlayHistoryEntry>> GetRecentHistoryAsync(int limit)
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT history_id, played_at, source_type, source_id, title, artist, duration_seconds, 
                   played_seconds, requested_by_user_id, requested_by_display_name, skipped, failed, failure_reason, guild_id
            FROM history
            ORDER BY played_at DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<PlayHistoryEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new PlayHistoryEntry
            {
                HistoryId = reader.GetInt64(0),
                PlayedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                SourceType = reader.GetString(2),
                SourceId = reader.GetString(3),
                Title = reader.IsDBNull(4) ? null : reader.GetString(4),
                Artist = reader.IsDBNull(5) ? null : reader.GetString(5),
                DurationSeconds = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                PlayedSeconds = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                RequestedByUserId = reader.GetString(8),
                RequestedByDisplayName = reader.GetString(9),
                Skipped = reader.GetInt32(10) == 1,
                Failed = reader.GetInt32(11) == 1,
                FailureReason = reader.IsDBNull(12) ? null : reader.GetString(12),
                GuildId = reader.GetString(13)
            });
        }
        return list;
    }
}
