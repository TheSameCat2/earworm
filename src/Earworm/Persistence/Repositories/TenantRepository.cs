using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Earworm.Persistence.Repositories;

public sealed class TenantRepository : ITenantRepository
{
    private readonly StateStore _stateStore;

    public TenantRepository(StateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public async Task<bool> IsAdmittedAsync(string guildId)
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM tenants
            WHERE guild_id = $guild_id AND status = 'active';
        ";
        cmd.Parameters.AddWithValue("$guild_id", guildId);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    public async Task AddTenantAsync(string guildId, string? ownerUserId)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO tenants (guild_id, owner_user_id, plan, status, created_at)
                VALUES ($guild_id, $owner_user_id, 'free', 'active', $created_at);
            ";
            cmd.Parameters.AddWithValue("$guild_id", guildId);
            cmd.Parameters.AddWithValue("$owner_user_id", ownerUserId as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task RemoveTenantAsync(string guildId)
    {
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tenants WHERE guild_id = $guild_id;";
            cmd.Parameters.AddWithValue("$guild_id", guildId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<IReadOnlyList<TenantRow>> GetAllTenantsAsync()
    {
        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT guild_id, owner_user_id, plan, status, created_at FROM tenants ORDER BY created_at;";

        var rows = new List<TenantRow>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new TenantRow(
                GuildId: reader.GetString(0),
                OwnerUserId: reader.IsDBNull(1) ? null : reader.GetString(1),
                Plan: reader.GetString(2),
                Status: reader.GetString(3),
                CreatedAt: reader.GetInt64(4)));
        }
        return rows;
    }
}
