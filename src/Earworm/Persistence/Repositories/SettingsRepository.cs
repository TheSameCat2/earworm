using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Earworm.Persistence.Repositories;

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly StateStore _stateStore;

    public SettingsRepository(StateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task<ulong?> GetDjRoleIdAsync(string guildId)
    {
        var val = await GetValueAsync(guildId, "dj_role_id");
        return ulong.TryParse(val, out var id) ? id : null;
    }

    public async Task SetDjRoleIdAsync(string guildId, ulong? roleId)
    {
        await SetValueAsync(guildId, "dj_role_id", roleId?.ToString());
    }

    public async Task<ulong?> GetLoggingChannelIdAsync(string guildId)
    {
        var val = await GetValueAsync(guildId, "logging_channel_id");
        return ulong.TryParse(val, out var id) ? id : null;
    }

    public async Task SetLoggingChannelIdAsync(string guildId, ulong? channelId)
    {
        await SetValueAsync(guildId, "logging_channel_id", channelId?.ToString());
    }

    public async Task<bool> IsDjEnabledAsync(string guildId)
    {
        var val = await GetValueAsync(guildId, "dj_enabled");
        return val == "1";
    }

    public async Task SetDjEnabledAsync(string guildId, bool enabled)
    {
        await SetValueAsync(guildId, "dj_enabled", enabled ? "1" : "0");
    }

    public async Task<ulong?> GetNowPlayingChannelIdAsync(string guildId)
    {
        var val = await GetValueAsync(guildId, "now_playing_channel_id");
        return ulong.TryParse(val, out var id) ? id : null;
    }

    public async Task SetNowPlayingChannelIdAsync(string guildId, ulong? channelId)
    {
        await SetValueAsync(guildId, "now_playing_channel_id", channelId?.ToString());
    }

    private async Task<string?> GetValueAsync(string guildId, string key)
    {
        // Read directly using concurrent connection
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE guild_id = $guildId AND key = $key;";
        cmd.Parameters.AddWithValue("$guildId", guildId);
        cmd.Parameters.AddWithValue("$key", key);

        var val = await cmd.ExecuteScalarAsync();
        return val as string;
    }

    private async Task SetValueAsync(string guildId, string key, string? value)
    {
        // Write serialized through the channel
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM settings WHERE guild_id = $guildId AND key = $key;";
                cmd.Parameters.AddWithValue("$guildId", guildId);
                cmd.Parameters.AddWithValue("$key", key);
            }
            else
            {
                cmd.CommandText = @"
                    INSERT INTO settings (guild_id, key, value, updated_at) VALUES ($guildId, $key, $value, $updated_at)
                    ON CONFLICT(guild_id, key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                ";
                cmd.Parameters.AddWithValue("$guildId", guildId);
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$value", value);
                cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            await cmd.ExecuteNonQueryAsync();
        });
    }
}
