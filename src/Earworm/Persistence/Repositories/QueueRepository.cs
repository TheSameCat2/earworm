using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;

namespace Earworm.Persistence.Repositories;

public sealed class QueueRepository : IQueueRepository
{
    private readonly StateStore _stateStore;

    public QueueRepository(StateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task<List<QueueItem>> GetQueueAsync()
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT queue_item_id, position, source_type, source_id, title, artist, duration_seconds, 
                   requested_by_user_id, requested_by_display_name, queued_at, guild_id
            FROM queue
            ORDER BY position ASC;
        ";

        var list = new List<QueueItem>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new QueueItem
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
        return list;
    }

    public async Task<long> AddTrackAsync(QueueItem item)
    {
        return await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                // Find current max position
                int nextPosition = 0;
                using (var maxCmd = connection.CreateCommand())
                {
                    maxCmd.Transaction = transaction;
                    maxCmd.CommandText = "SELECT MAX(position) FROM queue;";
                    var val = await maxCmd.ExecuteScalarAsync();
                    if (val != null && val != DBNull.Value)
                    {
                        nextPosition = Convert.ToInt32(val) + 1;
                    }
                }

                long newId;
                using (var insCmd = connection.CreateCommand())
                {
                    insCmd.Transaction = transaction;
                    insCmd.CommandText = @"
                        INSERT INTO queue (position, source_type, source_id, title, artist, duration_seconds,
                                           requested_by_user_id, requested_by_display_name, queued_at, guild_id)
                        VALUES ($position, $sourceType, $sourceId, $title, $artist, $duration,
                                $userId, $displayName, $queuedAt, $guildId)
                        RETURNING queue_item_id;
                    ";
                    insCmd.Parameters.AddWithValue("$position", nextPosition);
                    insCmd.Parameters.AddWithValue("$sourceType", item.SourceType);
                    insCmd.Parameters.AddWithValue("$sourceId", item.SourceId);
                    insCmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$artist", (object?)item.Artist ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$duration", (object?)item.DurationSeconds ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("$userId", item.RequestedByUserId);
                    insCmd.Parameters.AddWithValue("$displayName", item.RequestedByDisplayName);
                    insCmd.Parameters.AddWithValue("$queuedAt", item.QueuedAt.ToUnixTimeMilliseconds());
                    insCmd.Parameters.AddWithValue("$guildId", item.GuildId);

                    var idObj = await insCmd.ExecuteScalarAsync();
                    if (idObj == null || idObj == DBNull.Value)
                    {
                        throw new InvalidOperationException("INSERT ... RETURNING queue_item_id returned no rows.");
                    }
                    newId = Convert.ToInt64(idObj);
                }

                transaction.Commit();
                return newId;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        });
    }

    public async Task RemoveTrackAsync(long queueItemId)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                int? targetPosition = null;
                using (var posCmd = connection.CreateCommand())
                {
                    posCmd.Transaction = transaction;
                    posCmd.CommandText = "SELECT position FROM queue WHERE queue_item_id = $id;";
                    posCmd.Parameters.AddWithValue("$id", queueItemId);
                    var val = await posCmd.ExecuteScalarAsync();
                    if (val != null && val != DBNull.Value)
                    {
                        targetPosition = Convert.ToInt32(val);
                    }
                }

                if (targetPosition == null)
                {
                    transaction.Commit();
                    return;
                }

                using (var delCmd = connection.CreateCommand())
                {
                    delCmd.Transaction = transaction;
                    delCmd.CommandText = "DELETE FROM queue WHERE queue_item_id = $id;";
                    delCmd.Parameters.AddWithValue("$id", queueItemId);
                    await delCmd.ExecuteNonQueryAsync();
                }

                using (var shiftCmd = connection.CreateCommand())
                {
                    shiftCmd.Transaction = transaction;
                    shiftCmd.CommandText = "UPDATE queue SET position = position - 1 WHERE position > $position;";
                    shiftCmd.Parameters.AddWithValue("$position", targetPosition.Value);
                    await shiftCmd.ExecuteNonQueryAsync();
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

    public async Task MoveTrackAsync(long queueItemId, int toPosition)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                // Get all current queue items ordered by position
                var items = new List<(long Id, int Pos)>();
                using (var getCmd = connection.CreateCommand())
                {
                    getCmd.Transaction = transaction;
                    getCmd.CommandText = "SELECT queue_item_id, position FROM queue ORDER BY position ASC;";
                    using var reader = await getCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        items.Add((reader.GetInt64(0), reader.GetInt32(1)));
                    }
                }

                var targetItemIndex = items.FindIndex(x => x.Id == queueItemId);
                if (targetItemIndex == -1)
                {
                    transaction.Commit();
                    return;
                }

                var targetItem = items[targetItemIndex];
                items.RemoveAt(targetItemIndex);

                // Safe bounds clamping
                int clampedTo = Math.Max(0, Math.Min(toPosition, items.Count));
                items.Insert(clampedTo, targetItem);

                if (targetItemIndex == clampedTo)
                {
                    transaction.Commit();
                    return;
                }

                // Two-phase position rewrite to avoid UNIQUE(position) collisions:
                // phase 1 stages negative positions, phase 2 sets the final ones.
                for (int i = 0; i < items.Count; i++)
                {
                    using var updCmd = connection.CreateCommand();
                    updCmd.Transaction = transaction;
                    updCmd.CommandText = "UPDATE queue SET position = $pos WHERE queue_item_id = $id;";
                    updCmd.Parameters.AddWithValue("$pos", -(i + 1));
                    updCmd.Parameters.AddWithValue("$id", items[i].Id);
                    await updCmd.ExecuteNonQueryAsync();
                }

                for (int i = 0; i < items.Count; i++)
                {
                    using var updCmd = connection.CreateCommand();
                    updCmd.Transaction = transaction;
                    updCmd.CommandText = "UPDATE queue SET position = $pos WHERE queue_item_id = $id;";
                    updCmd.Parameters.AddWithValue("$pos", i);
                    updCmd.Parameters.AddWithValue("$id", items[i].Id);
                    await updCmd.ExecuteNonQueryAsync();
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

    public async Task ClearQueueAsync()
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM queue;";
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<PlaybackState> GetPlaybackStateAsync()
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT is_playing, is_paused, current_source_type, current_source_id, current_title, current_artist,
                   current_duration_seconds, current_requested_by_user_id, current_requested_by_display_name,
                   current_position_ms, voice_channel_id, voice_guild_id, updated_at
            FROM playback_state
            WHERE id = 1;
        ";

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new PlaybackState
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

        throw new InvalidOperationException("Playback state singleton row (id = 1) is missing from the database.");
    }

    public async Task UpdatePlaybackStateAsync(PlaybackState state)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE playback_state
                SET is_playing = $isPlaying,
                    is_paused = $isPaused,
                    current_source_type = $sourceType,
                    current_source_id = $sourceId,
                    current_title = $title,
                    current_artist = $artist,
                    current_duration_seconds = $duration,
                    current_requested_by_user_id = $userId,
                    current_requested_by_display_name = $displayName,
                    current_position_ms = $posMs,
                    voice_channel_id = $voiceChannelId,
                    voice_guild_id = $voiceGuildId,
                    updated_at = $updatedAt
                WHERE id = 1;
            ";
            cmd.Parameters.AddWithValue("$isPlaying", state.IsPlaying ? 1 : 0);
            cmd.Parameters.AddWithValue("$isPaused", state.IsPaused ? 1 : 0);
            cmd.Parameters.AddWithValue("$sourceType", (object?)state.CurrentSourceType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sourceId", (object?)state.CurrentSourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", (object?)state.CurrentTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$artist", (object?)state.CurrentArtist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$duration", (object?)state.CurrentDurationSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$userId", (object?)state.CurrentRequestedByUserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$displayName", (object?)state.CurrentRequestedByDisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$posMs", state.CurrentPositionMs);
            cmd.Parameters.AddWithValue("$voiceChannelId", (object?)state.VoiceChannelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$voiceGuildId", (object?)state.VoiceGuildId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task UpdatePlaybackPositionAsync(int positionMs)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE playback_state
                SET current_position_ms = $posMs,
                    updated_at = $updatedAt
                WHERE id = 1;
            ";
            cmd.Parameters.AddWithValue("$posMs", positionMs);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync();
        });
    }
}
