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

    public async Task SaveSnapshotAsync(string guildId, string savedByUserId)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                // 1. Upsert the snapshot row, stamping saved_at/saved_by and
                //    resetting the current_* fields (filled from playback_state below).
                using (var rowCmd = connection.CreateCommand())
                {
                    rowCmd.Transaction = transaction;
                    rowCmd.CommandText = @"
                        INSERT INTO snapshot (guild_id, saved_at, saved_by_user_id)
                        VALUES ($guildId, $savedAt, $userId)
                        ON CONFLICT(guild_id) DO UPDATE SET
                            saved_at = excluded.saved_at,
                            saved_by_user_id = excluded.saved_by_user_id,
                            current_source_type = NULL,
                            current_source_id = NULL,
                            current_title = NULL,
                            current_artist = NULL,
                            current_duration_seconds = NULL,
                            current_requested_by_user_id = NULL,
                            current_requested_by_display_name = NULL,
                            current_position_ms = NULL,
                            voice_channel_id = NULL,
                            voice_guild_id = NULL;
                    ";
                    rowCmd.Parameters.AddWithValue("$guildId", guildId);
                    rowCmd.Parameters.AddWithValue("$savedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    rowCmd.Parameters.AddWithValue("$userId", savedByUserId);
                    await rowCmd.ExecuteNonQueryAsync();
                }

                // 2. Copy the guild's current playback state into the snapshot row
                //    (no-op if the guild has never played anything).
                using (var copyPlayCmd = connection.CreateCommand())
                {
                    copyPlayCmd.Transaction = transaction;
                    copyPlayCmd.CommandText = @"
                        UPDATE snapshot
                        SET current_source_type = ps.current_source_type,
                            current_source_id = ps.current_source_id,
                            current_title = ps.current_title,
                            current_artist = ps.current_artist,
                            current_duration_seconds = ps.current_duration_seconds,
                            current_requested_by_user_id = ps.current_requested_by_user_id,
                            current_requested_by_display_name = ps.current_requested_by_display_name,
                            current_position_ms = ps.current_position_ms,
                            voice_channel_id = ps.voice_channel_id,
                            voice_guild_id = ps.voice_guild_id
                        FROM (SELECT * FROM playback_state WHERE guild_id = $guildId) ps
                        WHERE snapshot.guild_id = $guildId;
                    ";
                    copyPlayCmd.Parameters.AddWithValue("$guildId", guildId);
                    await copyPlayCmd.ExecuteNonQueryAsync();
                }

                // 3. Replace the snapshot queue from the guild's live queue.
                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM snapshot_queue WHERE snapshot_guild_id = $guildId;";
                    clearQueueCmd.Parameters.AddWithValue("$guildId", guildId);
                    await clearQueueCmd.ExecuteNonQueryAsync();
                }

                using (var copyQueueCmd = connection.CreateCommand())
                {
                    copyQueueCmd.Transaction = transaction;
                    copyQueueCmd.CommandText = @"
                        INSERT INTO snapshot_queue (snapshot_guild_id, position, source_type, source_id, title, artist,
                                                   duration_seconds, requested_by_user_id, requested_by_display_name, queued_at)
                        SELECT $guildId, position, source_type, source_id, title, artist,
                               duration_seconds, requested_by_user_id, requested_by_display_name, queued_at
                        FROM queue
                        WHERE guild_id = $guildId;
                    ";
                    copyQueueCmd.Parameters.AddWithValue("$guildId", guildId);
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

    public async Task<bool> HasSnapshotAsync(string guildId)
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT saved_at FROM snapshot WHERE guild_id = $guildId;";
        cmd.Parameters.AddWithValue("$guildId", guildId);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value;
    }

    public async Task<(PlaybackState PlaybackState, List<QueueItem> QueueItems)?> RestoreSnapshotAsync(string guildId)
    {
        (PlaybackState, List<QueueItem>)? result = null;

        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT saved_at FROM snapshot WHERE guild_id = $guildId;";
            checkCmd.Parameters.AddWithValue("$guildId", guildId);
            var hasSnapshot = (await checkCmd.ExecuteScalarAsync()) is not null and not DBNull;
            if (!hasSnapshot) return;

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM queue WHERE guild_id = $guildId;";
                    clearQueueCmd.Parameters.AddWithValue("$guildId", guildId);
                    await clearQueueCmd.ExecuteNonQueryAsync();
                }

                using (var copyQueueCmd = connection.CreateCommand())
                {
                    copyQueueCmd.Transaction = transaction;
                    copyQueueCmd.CommandText = @"
                        INSERT INTO queue (position, source_type, source_id, title, artist, duration_seconds,
                                           requested_by_user_id, requested_by_display_name, queued_at, guild_id)
                        SELECT position, source_type, source_id, title, artist, duration_seconds,
                               requested_by_user_id, requested_by_display_name, queued_at, $guildId
                        FROM snapshot_queue
                        WHERE snapshot_guild_id = $guildId;
                    ";
                    copyQueueCmd.Parameters.AddWithValue("$guildId", guildId);
                    await copyQueueCmd.ExecuteNonQueryAsync();
                }

                // Ensure a playback_state row exists for the guild, then copy from the snapshot.
                using (var ensureCmd = connection.CreateCommand())
                {
                    ensureCmd.Transaction = transaction;
                    ensureCmd.CommandText = "INSERT OR IGNORE INTO playback_state (guild_id, updated_at) VALUES ($guildId, $updatedAt);";
                    ensureCmd.Parameters.AddWithValue("$guildId", guildId);
                    ensureCmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await ensureCmd.ExecuteNonQueryAsync();
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
                        FROM (SELECT * FROM snapshot WHERE guild_id = $guildId) sn
                        WHERE playback_state.guild_id = $guildId;
                    ";
                    copyPlayCmd.Parameters.AddWithValue("$guildId", guildId);
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
                    WHERE guild_id = $guildId;
                ";
                playCmd.Parameters.AddWithValue("$guildId", guildId);

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
                    throw new InvalidOperationException("Playback state row is missing after restore.");
                }
            }

            var queueItems = new List<QueueItem>();
            using (var queueCmd = connection.CreateCommand())
            {
                queueCmd.CommandText = @"
                    SELECT queue_item_id, position, source_type, source_id, title, artist, duration_seconds,
                           requested_by_user_id, requested_by_display_name, queued_at, guild_id
                    FROM queue
                    WHERE guild_id = $guildId
                    ORDER BY position ASC;
                ";
                queueCmd.Parameters.AddWithValue("$guildId", guildId);

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

    public async Task ClearSnapshotAsync(string guildId)
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
                        WHERE guild_id = $guildId;
                    ";
                    clearPlayCmd.Parameters.AddWithValue("$guildId", guildId);
                    await clearPlayCmd.ExecuteNonQueryAsync();
                }

                using (var clearQueueCmd = connection.CreateCommand())
                {
                    clearQueueCmd.Transaction = transaction;
                    clearQueueCmd.CommandText = "DELETE FROM snapshot_queue WHERE snapshot_guild_id = $guildId;";
                    clearQueueCmd.Parameters.AddWithValue("$guildId", guildId);
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
