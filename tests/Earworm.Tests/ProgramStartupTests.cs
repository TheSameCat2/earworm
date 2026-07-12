using System.Reflection;
using Earworm.Config;
using Earworm.Persistence;
using Earworm.Persistence.Repositories;
using Earworm.Persistence.Schema;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Earworm.Tests;

public sealed class ProgramStartupTests
{
    [Fact]
    public async Task LegacyBackfill_DoesNotAdmitAChangedSeed_AfterInitialMigration()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"earworm_backfill_{Guid.NewGuid():N}.db");
        var firstConfig = BuildConfig(dbPath, "123");
        var store = new StateStore(firstConfig, NullLogger<StateStore>.Instance);
        try
        {
            new SchemaMigrator(store.ConnectionString, NullLogger<SchemaMigrator>.Instance).Migrate();
            await InvokeBackfillAsync(store.ConnectionString, firstConfig);

            var tenants = new TenantRepository(store);
            (await tenants.IsAdmittedAsync("123")).Should().BeTrue();

            await InvokeBackfillAsync(store.ConnectionString, BuildConfig(dbPath, "456"));

            (await tenants.IsAdmittedAsync("123")).Should().BeTrue();
            (await tenants.IsAdmittedAsync("456")).Should().BeFalse(
                "Discord.GuildId is only a migration seed, not a permanent admission path");
        }
        finally
        {
            store.Dispose();
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task LegacyBackfill_DoesNotReactivateASuspendedSeed()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"earworm_backfill_{Guid.NewGuid():N}.db");
        var config = BuildConfig(dbPath, "123");
        var store = new StateStore(config, NullLogger<StateStore>.Instance);
        try
        {
            new SchemaMigrator(store.ConnectionString, NullLogger<SchemaMigrator>.Instance).Migrate();
            await InvokeBackfillAsync(store.ConnectionString, config);

            var tenants = new TenantRepository(store);
            await tenants.RemoveTenantAsync("123");
            await InvokeBackfillAsync(store.ConnectionString, config);

            (await tenants.IsAdmittedAsync("123")).Should().BeFalse();
        }
        finally
        {
            store.Dispose();
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task LegacyBackfill_DoesNotReadmitSeed_WhenMigratedTenantTableIsEmpty()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"earworm_backfill_{Guid.NewGuid():N}.db");
        var config = BuildConfig(dbPath, "123");
        var store = new StateStore(config, NullLogger<StateStore>.Instance);
        try
        {
            new SchemaMigrator(store.ConnectionString, NullLogger<SchemaMigrator>.Instance).Migrate();
            await InvokeBackfillAsync(store.ConnectionString, config);
            await store.SubmitWriteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM tenants;";
                await command.ExecuteNonQueryAsync();
            });

            await InvokeBackfillAsync(store.ConnectionString, config);

            (await new TenantRepository(store).GetAllTenantsAsync()).Should().BeEmpty(
                "absence of the migration sentinel, not tenant count, makes the seed one-time");
        }
        finally
        {
            store.Dispose();
            DeleteSqliteFiles(dbPath);
        }
    }

    private static EarwormConfig BuildConfig(string dbPath, string guildId) => new()
    {
        Discord = new DiscordConfig { GuildId = guildId },
        Persistence = new PersistenceConfig { SqlitePath = dbPath }
    };

    private static async Task InvokeBackfillAsync(string connectionString, EarwormConfig config)
    {
        var method = typeof(Program).GetMethod(
            "BackfillLegacyTenantAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        await (Task)method!.Invoke(null, new object[] { connectionString, config })!;
    }

    private static void DeleteSqliteFiles(string dbPath)
    {
        foreach (string path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }
}
