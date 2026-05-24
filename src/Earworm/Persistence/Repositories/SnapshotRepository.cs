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
                using (var copyPlayCmd = connection.CreateCommand())
                {
                    copyPlayCmd.Transaction = transaction;
                    copyPlayCmd.CommandText = @"
                        UPDATE snapshot
                        SET saved_at = $savedAt,
                            saved_by_user_id = $userId,
                            current_source_type = ps.current_source_type,
                            current_source_id = ps.current_source_id,
                            current_title = ps.current_title,
                            current_artist = ps.current_artist,
                            current_duration_seconds = ps.current_duration_seconds,
                            current_requested_by_user_id = ps.current_requested_by_user_id,
                            current_requested_by_display_name = ps.current_requested_by_display_name,
                            current_position_ms = ps.current_position_ms,
                            voice_channel_id = ps.voice_channel_id,
                            voice_guild_id = ps.voice_guild_id
                        FROM (SELECT * FROM playback_state WHERE id = 1) ps
                        WHERE snapshot.id = 1;
                    ";
                    copyPlayCmd.Parameters.AddWithValue("$savedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    copyPlayCmd.Parameters.AddWithValue("$userId", savedByUserId);
                    await copyPlayCmd.ExecuteNonQueryAsync();
                }

                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM snapshot_queue WHERE snapshot_id = 1;";
                    await clearQueueCmd.ExecuteNonQueryAsync();
                }

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
        (PlaybackState, List<QueueItem>)? result = null;

        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT saved_at FROM snapshot WHERE id = 1;";
            var hasSnapshot = (await checkCmd.ExecuteScalarAsync()) is not null and not DBNull;
            if (!hasSnapshot) return;

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM queue;";
                    await clearQueueCmd.ExecuteNonQueryAsync();
                }

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

                using (var copyPlayCmd = connection.CreateCommand())
                {
                    copyPlayCmd.Transaction = transaction;
                    copyPlayCmd.CommandText = @"
                        UPDATE playback_state
                        SET is_playing = CASE WHEN sn.current_source_id IS NOT NULL THEN 1 ELSE 0 END,
                            is_paused = 0,
                            current_source_type = sn.current_source_type,
                            current_source_id = sn.current_source_id,
                            current_title = sn.current_title,
                            current_artist = sn.current_artist,
                            current_duration_seconds = sn.current_duration_seconds,
                            current_requested_by_user_id = sn.current_requested_by_user_id,
                            current_requested_by_display_name = sn.current_requested_by_display_name,
                            current_position_ms = COALESCE(sn.current_position_ms, 0),
                            voice_channel_id = sn.voice_channel_id,
                            voice_guild_id = sn.voice_guild_id,
                            updated_at = $updatedAt
                        FROM (SELECT * FROM snapshot WHERE id = 1) sn
                        WHERE playback_state.id = 1;
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

            PlaybackState playbackState;
            using (var playCmd = connection.CreateCommand())
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
            using (var queueCmd = connection.CreateCommand())
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

            result = (playbackState, queueItems);
        });

        return result;
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
