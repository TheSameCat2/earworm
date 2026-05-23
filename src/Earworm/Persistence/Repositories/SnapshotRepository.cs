using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public sealed class SnapshotRepository : ISnapshotRepository
{
    private readonly StateStore _stateStore;

    public SnapshotRepository(StateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task SaveSnapshotAsync(string savedByUserId)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                // 1. Copy active playback_state to snapshot
                using (var copyPlayCmd = connection.CreateCommand())
                {
                    copyPlayCmd.Transaction = transaction;
                    copyPlayCmd.CommandText = @"
                        UPDATE snapshot
                        SET saved_at = $savedAt,
                            saved_by_user_id = $userId,
                            current_source_type = (SELECT current_source_type FROM playback_state WHERE id = 1),
                            current_source_id = (SELECT current_source_id FROM playback_state WHERE id = 1),
                            current_title = (SELECT current_title FROM playback_state WHERE id = 1),
                            current_artist = (SELECT current_artist FROM playback_state WHERE id = 1),
                            current_duration_seconds = (SELECT current_duration_seconds FROM playback_state WHERE id = 1),
                            current_requested_by_user_id = (SELECT current_requested_by_user_id FROM playback_state WHERE id = 1),
                            current_requested_by_display_name = (SELECT current_requested_by_display_name FROM playback_state WHERE id = 1),
                            current_position_ms = (SELECT current_position_ms FROM playback_state WHERE id = 1),
                            voice_channel_id = (SELECT voice_channel_id FROM playback_state WHERE id = 1),
                            voice_guild_id = (SELECT voice_guild_id FROM playback_state WHERE id = 1)
                        WHERE id = 1;
                    ";
                    copyPlayCmd.Parameters.AddWithValue("$savedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    copyPlayCmd.Parameters.AddWithValue("$userId", savedByUserId);
                    await copyPlayCmd.ExecuteNonQueryAsync();
                }

                // 2. Clear previous snapshot_queue items
                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM snapshot_queue WHERE snapshot_id = 1;";
                    await clearQueueCmd.ExecuteNonQueryAsync();
                }

                // 3. Copy active queue to snapshot_queue
                using (var copyQueueCmd = connection.CreateCommand())
                {
                    copyQueueCmd.Transaction = transaction;
                    copyQueueCmd.CommandText = @"
                        INSERT INTO snapshot_queue (snapshot_id, position, source_type, source_id, title, artist, 
                                                   duration_seconds, requested_by_user_id, requested_by_display_name, queued_at, guild_id)
                        SELECT 1, position, source_type, source_id, title, artist, 
                               duration_seconds, requested_by_user_id, requested_by_display_name, queued_at, guild_id
                        FROM queue;
                    ";
                    await copyQueueCmd.ExecuteNonQueryAsync();
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

    public async Task<bool> HasSnapshotAsync()
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT saved_at FROM snapshot WHERE id = 1;";
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value;
    }

    public async Task<(PlaybackState PlaybackState, List<QueueItem> QueueItems)?> RestoreSnapshotAsync()
    {
        if (!await HasSnapshotAsync())
        {
            return null;
        }

        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                // 1. Clear active queue
                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM queue;";
                    await clearQueueCmd.ExecuteNonQueryAsync();
                }

                // 2. Copy snapshot_queue to active queue
                using (var copyQueueCmd = connection.CreateCommand())
                {
                    copyQueueCmd.Transaction = transaction;
                    copyQueueCmd.CommandText = @"
                        INSERT INTO queue (position, source_type, source_id, title, artist, duration_seconds, 
                                           requested_by_user_id, requested_by_display_name, queued_at, guild_id)
                        SELECT position, source_type, source_id, title, artist, duration_seconds, 
                               requested_by_user_id, requested_by_display_name, queued_at, guild_id
                        FROM snapshot_queue
                        WHERE snapshot_id = 1;
                    ";
                    await copyQueueCmd.ExecuteNonQueryAsync();
                }

                // 3. Copy snapshot playback details to playback_state
                using (var copyPlayCmd = connection.CreateCommand())
                {
                    copyPlayCmd.Transaction = transaction;
                    copyPlayCmd.CommandText = @"
                        UPDATE playback_state
                        SET is_playing = CASE WHEN (SELECT current_source_id FROM snapshot WHERE id = 1) IS NOT NULL THEN 1 ELSE 0 END,
                            is_paused = 0,
                            current_source_type = (SELECT current_source_type FROM snapshot WHERE id = 1),
                            current_source_id = (SELECT current_source_id FROM snapshot WHERE id = 1),
                            current_title = (SELECT current_title FROM snapshot WHERE id = 1),
                            current_artist = (SELECT current_artist FROM snapshot WHERE id = 1),
                            current_duration_seconds = (SELECT current_duration_seconds FROM snapshot WHERE id = 1),
                            current_requested_by_user_id = (SELECT current_requested_by_user_id FROM snapshot WHERE id = 1),
                            current_requested_by_display_name = (SELECT current_requested_by_display_name FROM snapshot WHERE id = 1),
                            current_position_ms = COALESCE((SELECT current_position_ms FROM snapshot WHERE id = 1), 0),
                            voice_channel_id = (SELECT voice_channel_id FROM snapshot WHERE id = 1),
                            voice_guild_id = (SELECT voice_guild_id FROM snapshot WHERE id = 1),
                            updated_at = $updatedAt
                        WHERE id = 1;
                    ";
                    copyPlayCmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await copyPlayCmd.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        });

        // Fetch restored active queue and playback state to return to the caller
        using var readConnection = _stateStore.CreateConnection();
        await readConnection.OpenAsync();

        PlaybackState playbackState;
        using (var playCmd = readConnection.CreateCommand())
        {
            playCmd.CommandText = @"
                SELECT is_playing, is_paused, current_source_type, current_source_id, current_title, current_artist,
                       current_duration_seconds, current_requested_by_user_id, current_requested_by_display_name,
                       current_position_ms, voice_channel_id, voice_guild_id, updated_at
                FROM playback_state
                WHERE id = 1;
            ";

            using var reader = await playCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                playbackState = new PlaybackState
                {
                    IsPlaying = reader.GetInt32(0) == 1,
                    IsPaused = reader.GetInt32(1) == 1,
                    CurrentSourceType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    CurrentSourceId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CurrentTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CurrentArtist = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CurrentDurationSeconds = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    CurrentRequestedByUserId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CurrentRequestedByDisplayName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    CurrentPositionMs = reader.GetInt32(9),
                    VoiceChannelId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    VoiceGuildId = reader.IsDBNull(11) ? null : reader.GetString(11),
                    UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12))
                };
            }
            else
            {
                throw new InvalidOperationException("Playback state singleton row is missing after restore.");
            }
        }

        var queueItems = new List<QueueItem>();
        using (var queueCmd = readConnection.CreateCommand())
        {
            queueCmd.CommandText = @"
                SELECT queue_item_id, position, source_type, source_id, title, artist, duration_seconds, 
                       requested_by_user_id, requested_by_display_name, queued_at, guild_id
                FROM queue
                ORDER BY position ASC;
            ";

            using var reader = await queueCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                queueItems.Add(new QueueItem
                {
                    QueueItemId = reader.GetInt64(0),
                    Position = reader.GetInt32(1),
                    SourceType = reader.GetString(2),
                    SourceId = reader.GetString(3),
                    Title = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Artist = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DurationSeconds = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    RequestedByUserId = reader.GetString(7),
                    RequestedByDisplayName = reader.GetString(8),
                    QueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                    GuildId = reader.GetString(10)
                });
            }
        }

        return (playbackState, queueItems);
    }

    public async Task ClearSnapshotAsync()
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var clearPlayCmd = connection.CreateCommand())
                {
                    clearPlayCmd.Transaction = transaction;
                    clearPlayCmd.CommandText = @"
                        UPDATE snapshot
                        SET saved_at = NULL,
                            saved_by_user_id = NULL,
                            current_source_type = NULL,
                            current_source_id = NULL,
                            current_title = NULL,
                            current_artist = NULL,
                            current_duration_seconds = NULL,
                            current_requested_by_user_id = NULL,
                            current_requested_by_display_name = NULL,
                            current_position_ms = NULL,
                            voice_channel_id = NULL,
                            voice_guild_id = NULL
                        WHERE id = 1;
                    ";
                    await clearPlayCmd.ExecuteNonQueryAsync();
                }

                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM snapshot_queue WHERE snapshot_id = 1;";
                    await clearQueueCmd.ExecuteNonQueryAsync();
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
}
