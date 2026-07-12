using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Earworm.Domain.Tenants;
using Microsoft.Data.Sqlite;

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
        if (!DiscordGuildId.TryNormalize(guildId, out var canonicalGuildId))
        {
            return false;
        }

        using var connection = _stateStore.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM tenants
            WHERE guild_id = $guild_id AND status = 'active';
        ";
        cmd.Parameters.AddWithValue("$guild_id", canonicalGuildId);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    public async Task AddTenantAsync(string guildId, string? ownerUserId)
    {
        var canonicalGuildId = DiscordGuildId.Normalize(guildId, nameof(guildId));
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO tenants (guild_id, owner_user_id, plan, status, created_at)
                VALUES ($guild_id, $owner_user_id, 'free', 'active', $created_at)
                ON CONFLICT(guild_id) DO UPDATE SET status = 'active', owner_user_id = excluded.owner_user_id;
            ";
            cmd.Parameters.AddWithValue("$guild_id", canonicalGuildId);
            cmd.Parameters.AddWithValue("$owner_user_id", ownerUserId as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task RemoveTenantAsync(string guildId)
    {
        var canonicalGuildId = DiscordGuildId.Normalize(guildId, nameof(guildId));
        await _stateStore.SubmitWriteAsync(async connection =>
        {
            // Removing access is deliberately a suspension, not data deletion.
            // Keeping the queue, history, settings, metrics, and snapshots makes
            // a later re-admit lossless and matches the public admin contract.
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE tenants SET status = 'suspended' WHERE guild_id = $guild_id;";
            cmd.Parameters.AddWithValue("$guild_id", canonicalGuildId);
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

    /// <summary>
    /// Canonicalizes numeric tenant aliases left by older releases (for
    /// example, <c>00123</c> -&gt; <c>123</c>) together with all of their
    /// per-guild rows. The entire operation is one transaction. Ambiguous
    /// aliases and unique-key collisions fail closed and roll back.
    /// </summary>
    public Task<int> NormalizeLegacyGuildIdsAsync() =>
        _stateStore.SubmitWriteAsync(async connection =>
        {
            var rawGuildIds = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT guild_id FROM tenants ORDER BY guild_id;";
                using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rawGuildIds.Add(reader.GetString(0));
                }
            }

            var identities = new List<(string Raw, string Canonical)>();
            foreach (var rawGuildId in rawGuildIds)
            {
                if (!DiscordGuildId.TryNormalize(rawGuildId, out var canonicalGuildId))
                {
                    throw new TenantIdentityNormalizationException(
                        $"Tenant row '{rawGuildId}' is not a numeric Discord guild ID. " +
                        "No tenant identities were changed; repair or remove the invalid row before restarting.");
                }

                identities.Add((rawGuildId, canonicalGuildId));
            }

            var ambiguous = identities
                .GroupBy(identity => identity.Canonical, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (ambiguous is not null)
            {
                var aliases = string.Join(", ", ambiguous.Select(identity => $"'{identity.Raw}'"));
                throw new TenantIdentityNormalizationException(
                    $"Tenant aliases {aliases} all identify guild '{ambiguous.Key}'. " +
                    "No tenant identities were changed because automatically merging tenant data is unsafe.");
            }

            var aliasesToNormalize = identities
                .Where(identity => !string.Equals(identity.Raw, identity.Canonical, StringComparison.Ordinal))
                .ToArray();
            if (aliasesToNormalize.Length == 0)
            {
                return 0;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                // snapshot_queue references snapshot(guild_id) without an ON
                // UPDATE action. Defer that FK until both sides have moved.
                using (var defer = connection.CreateCommand())
                {
                    defer.Transaction = transaction;
                    defer.CommandText = "PRAGMA defer_foreign_keys = ON;";
                    await defer.ExecuteNonQueryAsync();
                }

                foreach (var (rawGuildId, canonicalGuildId) in aliasesToNormalize)
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = @"
                        UPDATE queue            SET guild_id = $canonical WHERE guild_id = $raw;
                        UPDATE history          SET guild_id = $canonical WHERE guild_id = $raw;
                        UPDATE playback_state   SET guild_id = $canonical WHERE guild_id = $raw;
                        UPDATE settings         SET guild_id = $canonical WHERE guild_id = $raw;
                        UPDATE metrics_global   SET guild_id = $canonical WHERE guild_id = $raw;
                        UPDATE metrics_per_user SET guild_id = $canonical WHERE guild_id = $raw;
                        UPDATE snapshot_queue   SET snapshot_guild_id = $canonical WHERE snapshot_guild_id = $raw;
                        UPDATE snapshot         SET guild_id = $canonical WHERE guild_id = $raw;

                        UPDATE playback_state SET voice_guild_id = $canonical WHERE voice_guild_id = $raw;
                        UPDATE snapshot       SET voice_guild_id = $canonical WHERE voice_guild_id = $raw;

                        UPDATE tenants SET guild_id = $canonical WHERE guild_id = $raw;
                    ";
                    update.Parameters.AddWithValue("$raw", rawGuildId);
                    update.Parameters.AddWithValue("$canonical", canonicalGuildId);
                    await update.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                return aliasesToNormalize.Length;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                transaction.Rollback();
                throw new TenantIdentityNormalizationException(
                    "Legacy guild IDs could not be canonicalized without overwriting existing per-guild data. " +
                    "No normalization changes were committed.",
                    ex);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        });
}
