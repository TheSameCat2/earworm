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

    public async Task<ulong?> GetDjRoleIdAsync()
    {
        var val = await GetValueAsync("dj_role_id");
        return ulong.TryParse(val, out var id) ? id : null;
    }

    public async Task SetDjRoleIdAsync(ulong? roleId)
    {
        await SetValueAsync("dj_role_id", roleId?.ToString());
    }

    public async Task<ulong?> GetLoggingChannelIdAsync()
    {
        var val = await GetValueAsync("logging_channel_id");
        return ulong.TryParse(val, out var id) ? id : null;
    }

    public async Task SetLoggingChannelIdAsync(ulong? channelId)
    {
        await SetValueAsync("logging_channel_id", channelId?.ToString());
    }

    public async Task<bool> IsDjEnabledAsync()
    {
        var val = await GetValueAsync("dj_enabled");
        return val == "1";
    }

    public async Task SetDjEnabledAsync(bool enabled)
    {
        await SetValueAsync("dj_enabled", enabled ? "1" : "0");
    }

    private async Task<string?> GetValueAsync(string key)
    {
        // Read directly using concurrent connection
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);

        var val = await cmd.ExecuteScalarAsync();
        return val as string;
    }

    private async Task SetValueAsync(string key, string? value)
    {
        // Write serialized through the channel
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM settings WHERE key = $key;";
                cmd.Parameters.AddWithValue("$key", key);
            }
            else
            {
                cmd.CommandText = @"
                    INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $updated_at)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                ";
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$value", value);
                cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            await cmd.ExecuteNonQueryAsync();
        });
    }
}
