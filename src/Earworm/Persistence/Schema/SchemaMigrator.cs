using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Earworm.Persistence.Schema;

public sealed class SchemaMigrator
{
    private readonly string _connectionString;
    private readonly ILogger<SchemaMigrator> _logger;

    public SchemaMigrator(string connectionString, ILogger<SchemaMigrator> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public void Migrate()
    {
        _logger.LogInformation("Running database migrations...");

        var migrations = DiscoverMigrations();
        if (migrations.Count == 0)
        {
            _logger.LogInformation("No database migrations found.");
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Enable optimal WAL mode pragmas before doing anything
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode = WAL;";
            pragmaCmd.ExecuteNonQuery();
        }

        // Check if schema_migrations table exists
        bool tableExists;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
            tableExists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        var appliedMigrationIds = new HashSet<int>();
        if (tableExists)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT migration_id FROM schema_migrations;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                appliedMigrationIds.Add(reader.GetInt32(0));
            }
        }

        var pendingMigrations = migrations
            .Where(m => !appliedMigrationIds.Contains(m.Id))
            .OrderBy(m => m.Id)
            .ToList();

        if (pendingMigrations.Count == 0)
        {
            _logger.LogInformation("Database is up-to-date. No pending migrations.");
            return;
        }

        _logger.LogInformation($"Found {pendingMigrations.Count} pending migrations to apply.");

        foreach (var migration in pendingMigrations)
        {
            _logger.LogInformation($"Applying migration {migration.Id}: {migration.Name}...");

            using var transaction = connection.BeginTransaction();
            try
            {
                // Execute migration SQL
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = migration.Sql;
                    cmd.ExecuteNonQuery();
                }

                // Record migration in schema_migrations
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "INSERT INTO schema_migrations (migration_id, name, applied_at) VALUES ($id, $name, $applied_at);";
                    cmd.Parameters.AddWithValue("$id", migration.Id);
                    cmd.Parameters.AddWithValue("$name", migration.Name);
                    cmd.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                _logger.LogInformation($"Successfully applied migration {migration.Id}.");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, $"Failed to apply migration {migration.Id}: {migration.Name}. Transaction rolled back.");
                throw;
            }
        }

        _logger.LogInformation("Database migration completed successfully.");
    }

    private List<MigrationItem> DiscoverMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();

        var list = new List<MigrationItem>();

        foreach (var resourceName in resourceNames)
        {
            // Resource names look like: Earworm.Persistence.Schema.Migrations.001_initial.sql
            if (!resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = resourceName.Split('.');
            if (parts.Length < 2) continue;

            var filename = parts[^2]; // "001_initial"
            var filenameParts = filename.Split('_', 2);
            if (filenameParts.Length != 2) continue;

            if (!int.TryParse(filenameParts[0], out var id))
                continue;

            var name = filenameParts[1];

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            list.Add(new MigrationItem(id, name, sql));
        }

        return list;
    }

    private record MigrationItem(int Id, string Name, string Sql);
}
