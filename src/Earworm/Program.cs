using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Lavalink4NET;
using Lavalink4NET.Extensions;
using Lavalink4NET.InactivityTracking.Extensions;
using Earworm.Config;
using Earworm.Infrastructure;
using Earworm.Persistence;
using Earworm.Persistence.Schema;
using Earworm.Persistence.Repositories;
using Earworm.Domain.Queue;
using Earworm.Domain.Player;
using Earworm.Domain.DJ;
using Earworm.Domain.Tenants;
using Earworm.Discord;
using Earworm.Discord.Commands;
using Earworm.Health;

namespace Earworm;

public static class Program
{
    public static async Task Main(string[] args)
    {
        TryLoadDotEnv();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"

  ___  __ _ _ ____      _____  _ __ _ __ ___
 / _ \/ _` | '__\ \ /\ / / _ \| '__| '_ ` _ \
|  __/ (_| | |   \ V  V / (_) | |  | | | | | |
 \___|\__,_|_|    \_/\_/ \___/|_|  |_| |_| |_|

");
        Console.ResetColor();
        Console.WriteLine("Initializing earworm Discord Music Bot (Lavalink edition)...");

        var env = Environment.GetEnvironmentVariable("NET_ENVIRONMENT") ?? "Production";
        Console.WriteLine($"Environment: {env}");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddYamlFile("conf/earworm.yaml", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "EARWORM_")
            .Build();

        var earwormConfig = new EarwormConfig();
        configuration.Bind(earwormConfig);

        try
        {
            ValidateConfig(earwormConfig);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Configuration error: {ex.Message}");
            Console.ResetColor();
            Environment.ExitCode = 1;
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Configuration loaded and verified successfully.");
        Console.ResetColor();

        // ValidateConfig above already rejects startup if the token is missing,
        // so the environment variable is guaranteed non-null here. Reading it
        // again (rather than carrying a value through ValidateConfig) keeps the
        // two code paths independent and avoids a dead "PLACEHOLDER" fallback.
        var botToken = Environment.GetEnvironmentVariable("EARWORM_DISCORD_BOT_TOKEN")!;

        var services = new ServiceCollection();
        ConfigureServices(services, earwormConfig, botToken);

        // await using ensures all IDisposable / IAsyncDisposable singletons are
        // torn down on Main exit — most importantly StateStore, which drains
        // the SQLite write channel and checkpoints WAL on Dispose.
        await using var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<object>>();
        var shutdownLifetime = serviceProvider.GetRequiredService<ShutdownLifetime>();

        // Install signal handlers before any startup operation that can block or
        // retry. Lavalink retry is intentionally unbounded, so SIGTERM/SIGINT
        // must be able to cancel it even before startup completes.
        var shutdownRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Shutdown requested (SIGINT).");
            shutdownLifetime.Cancel();
            shutdownRequested.TrySetResult();
        };
        using var sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            logger.LogInformation("Shutdown requested (SIGTERM).");
            shutdownLifetime.Cancel();
            shutdownRequested.TrySetResult();
        });

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogCritical(e.Exception, "Unobserved task exception (secondary fault in fire-and-forget work).");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            logger.LogCritical(ex, "Unhandled AppDomain exception. IsTerminating={Terminating}", e.IsTerminating);
        };

        var client = serviceProvider.GetRequiredService<DiscordClient>();

        // Migrations + multi-tenant backfill. Must run before slash registration
        // (which reads the tenants table) and before any per-guild engine hydrates.
        try
        {
            var stateStore = serviceProvider.GetRequiredService<StateStore>();
            var migrationLogger = serviceProvider.GetRequiredService<ILogger<SchemaMigrator>>();
            new SchemaMigrator(stateStore.ConnectionString, migrationLogger).Migrate();

            if (DiscordGuildId.TryNormalize(earwormConfig.Discord.GuildId, out _))
            {
                await BackfillLegacyTenantAsync(stateStore.ConnectionString, earwormConfig);
            }
            else
            {
                logger.LogWarning("Skipped tenant backfill: Discord.GuildId '{GuildId}' is not a valid numeric snowflake.", earwormConfig.Discord.GuildId);
            }

            var tenantService = serviceProvider.GetRequiredService<ITenantService>();
            int normalizedTenants = await tenantService.NormalizeLegacyGuildIdsAsync();
            if (normalizedTenants > 0)
            {
                logger.LogInformation("Canonicalized {Count} legacy tenant guild ID(s).", normalizedTenants);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed. Shutting down.");
            Environment.ExitCode = 1;
            return;
        }

        if (shutdownLifetime.IsShuttingDown) return;

        // Slash commands. UseSlashCommands accepts a ServiceProvider; command
        // classes are resolved from it per-invocation. Registration is per active
        // tenant (instant propagation) via TenantLifecycleListener.
        var slash = client.UseSlashCommands(new SlashCommandsConfiguration { Services = serviceProvider });

        var lifecycle = serviceProvider.GetRequiredService<TenantLifecycleListener>();
        try
        {
            lifecycle.Attach(slash);
            await lifecycle.RegisterStartupCommandsAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Slash command registration failed. Shutting down.");
            Environment.ExitCode = 1;
            return;
        }

        if (shutdownLifetime.IsShuttingDown) return;

        // Convert failed command checks (not-whitelisted, not-DJ, …) into an
        // ephemeral reply instead of a silent no-op.
        slash.SlashCommandErrored += async (_, e) =>
        {
            if (e.Exception is SlashExecutionChecksFailedException)
            {
                try
                {
                    await e.Context.CreateResponseAsync(
                        InteractionResponseType.ChannelMessageWithSource,
                        new DiscordInteractionResponseBuilder
                        {
                            IsEphemeral = true,
                            Content = "⛔ Not available here — this server isn't authorized, or you lack permission for this command."
                        });
                }
                catch
                {
                    // Interaction may already be acknowledged; ignore.
                }
                return;
            }

            // Any other failure — including an exception THROWN by a check (e.g. a
            // transient SQLite error in IsAdmittedAsync / GetDjRoleIdAsync) rather
            // than the check returning false — would otherwise leave the user
            // staring at "the application did not respond". Log it and best-effort
            // surface a generic error.
            logger.LogError(e.Exception, "Slash command '{Name}' errored.", e.Context?.CommandName);
            if (e.Context == null) return; // nothing to respond to
            try
            {
                await e.Context.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder
                    {
                        IsEphemeral = true,
                        Content = "⚠️ Something went wrong running that command. Please try again."
                    });
            }
            catch
            {
                // Already acknowledged/deferred (command body failed after
                // responding) — fall back to editing the deferred response.
                try
                {
                    await e.Context.EditResponseAsync(
                        new DiscordWebhookBuilder().WithContent("⚠️ Something went wrong running that command. Please try again."));
                }
                catch
                {
                    // Interaction expired or already finalized; nothing more to do.
                }
            }
        };

        // HTTP host (PRD §11 — /health, /metrics, /tts/{id}).
        var healthEndpoint = serviceProvider.GetRequiredService<HealthEndpoint>();
        try
        {
            await healthEndpoint.StartAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to start in-process HTTP host. Shutting down.");
            Environment.ExitCode = 1;
            return;
        }

        if (shutdownLifetime.IsShuttingDown) return;

        // TTS scratch directory: sweep orphans from any previous crash, then
        // start the periodic retention loop.
        var janitor = serviceProvider.GetRequiredService<TtsScratchJanitor>();
        janitor.SweepOnStartup();
        janitor.StartPeriodicSweep();

        // Eagerly resolve event-handler singletons so their PerGuildRegistry
        // initializers and gateway subscriptions are wired before any per-guild
        // engine is created or any gateway event fires.
        _ = serviceProvider.GetRequiredService<MessageListener>();
        _ = serviceProvider.GetRequiredService<NowPlayingPoster>();
        _ = serviceProvider.GetRequiredService<VoiceManager>();
        _ = serviceProvider.GetRequiredService<TrackFailureHandler>();

        // Pre-create + hydrate each active tenant's engines: PlayerEngine so it
        // subscribes to Lavalink events and wires its DJ hook, QueueManager so
        // its persisted queue comes back after a restart.
        try
        {
            var tenantService = serviceProvider.GetRequiredService<ITenantService>();
            var queueRegistry = serviceProvider.GetRequiredService<PerGuildRegistry<QueueManager>>();
            var playerRegistry = serviceProvider.GetRequiredService<PerGuildRegistry<PlayerEngine>>();

            foreach (var tenant in await tenantService.GetAllTenantsAsync())
            {
                if (!string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ulong.TryParse(tenant.GuildId, out _)) continue;
                try
                {
                    playerRegistry.GetOrCreate(tenant.GuildId);
                    await queueRegistry.GetOrCreate(tenant.GuildId).InitializeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to initialize tenant {GuildId}; continuing.", tenant.GuildId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Per-guild engine initialization failed. Shutting down.");
            Environment.ExitCode = 1;
            return;
        }

        if (shutdownLifetime.IsShuttingDown) return;

        // Discord gateway connect.
        var gateway = serviceProvider.GetRequiredService<DiscordGateway>();
        try
        {
            await gateway.StartAsync(shutdownLifetime.Token);
            await lifecycle.ClearSuspendedGuildCommandsAsync();
        }
        catch (OperationCanceledException) when (shutdownLifetime.IsShuttingDown)
        {
            logger.LogWarning("Discord Gateway startup cancelled because shutdown was requested.");
            return;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to start Discord Gateway. Shutting down.");
            Environment.ExitCode = 1;
            return;
        }

        // Lavalink connect. Must come AFTER Discord client is connected so
        // Lavalink4NET can read currentUser.Id for its WebSocket handshake.
        // In Docker, the Lavalink container frequently starts slower than the bot
        // container. Retry with exponential backoff instead of crashing so the
        // orchestrator can rely on the health check and we don't hammer the
        // Lavalink server with connection attempts.
        var audioService = serviceProvider.GetRequiredService<IAudioService>();
        try
        {
            await StartLavalinkWithBackoffAsync(audioService, shutdownLifetime, logger);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Lavalink connection retry cancelled because shutdown was requested.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to start Lavalink audio service after retries. Is the Lavalink server running and reachable?");
            Environment.ExitCode = 1;
            return;
        }

        if (!shutdownLifetime.IsShuttingDown)
        {
            logger.LogInformation("earworm services started. Press Ctrl+C to shut down.");
            await shutdownRequested.Task;
        }
        logger.LogInformation("Shutting down...");
        shutdownLifetime.Cancel();

        try { await audioService.StopAsync(); }
        catch (Exception ex) { logger.LogError(ex, "Error stopping Lavalink audio service."); }

        try { await gateway.StopAsync(); }
        catch (Exception ex) { logger.LogError(ex, "Error stopping Discord Gateway."); }

        try { await healthEndpoint.DisposeAsync(); }
        catch (Exception ex) { logger.LogError(ex, "Error stopping HTTP host."); }

        logger.LogInformation("earworm stopped gracefully.");
    }

    public static void ConfigureServices(IServiceCollection services, EarwormConfig config, string botToken)
    {
        services.AddSingleton(config);
        services.AddSingleton<ShutdownLifetime>();

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            var envLogLevel = Environment.GetEnvironmentVariable("EARWORM_LOG_LEVEL");
            builder.SetMinimumLevel(
                Enum.TryParse<LogLevel>(envLogLevel, true, out var level) ? level : LogLevel.Warning);
        });

        // DiscordClient is a singleton resolved with the DI-supplied LoggerFactory.
        // Lavalink4NET.DSharpPlus picks it up via its IDiscordClientWrapper.
        services.AddSingleton<DiscordClient>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new DiscordClient(new DiscordConfiguration
            {
                Token = botToken,
                TokenType = TokenType.Bot,
                Intents = DiscordIntents.AllUnprivileged
                    | DiscordIntents.GuildVoiceStates
                    | DiscordIntents.MessageContents,
                LoggerFactory = loggerFactory,
                MinimumLogLevel = LogLevel.Information
            });
        });

        // Lavalink4NET. AddLavalink picks the DSharpPlus integration from the
        // referenced Lavalink4NET.DSharpPlus package.
        services.AddLavalink();
        services.ConfigureLavalink(options =>
        {
            options.BaseAddress = new Uri($"http://{config.Lavalink.Host}:{config.Lavalink.Port}");
            options.Passphrase = config.Lavalink.Password;
            options.ReadyTimeout = TimeSpan.FromSeconds(30);
        });
        services.AddInactivityTracking();

        services.AddSingleton<StateStore>();

        services.AddSingleton<IQueueRepository, QueueRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<ISnapshotRepository, SnapshotRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<IMetricsRepository, MetricsRepository>();
        services.AddSingleton<ITenantRepository, TenantRepository>();
        services.AddSingleton<ITenantService, TenantService>();

        // Per-call timeouts. Without these, a hung external API blocks the
        // caller for the OS-default ~100s TCP timeout, which during DJ commentary
        // generation freezes PlayNextAsync and stalls music playback.
        services.AddHttpClient(nameof(GeminiClient), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(nameof(ElevenLabsTtsProvider), c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<GeminiClient>();
        services.AddSingleton<ElevenLabsTtsProvider>();
        services.AddSingleton<ITtsProvider>(sp => sp.GetRequiredService<ElevenLabsTtsProvider>());
        services.AddSingleton<TtsScratchJanitor>();

        // Per-guild stateful engines. Each guild gets its own QueueManager,
        // AudioTransitionController, DJEngine, and PlayerEngine, created lazily
        // and cached by PerGuildRegistry<T>. The PlayerEngine factory also wires
        // that guild's DJ pre-track hook, replacing the old global SetPreTrackHook.
        services.AddSingleton(sp => new PerGuildRegistry<QueueManager>(gid => new QueueManager(
            sp.GetRequiredService<IQueueRepository>(),
            sp.GetRequiredService<ISnapshotRepository>(),
            sp.GetRequiredService<EarwormConfig>(),
            sp.GetRequiredService<ILogger<QueueManager>>(),
            gid)));

        services.AddSingleton(sp => new PerGuildRegistry<AudioTransitionController>(_ => new AudioTransitionController(
            sp.GetRequiredService<EarwormConfig>(),
            sp.GetRequiredService<ILogger<AudioTransitionController>>())));

        services.AddSingleton(sp => new PerGuildRegistry<DJEngine>(gid => new DJEngine(
            sp.GetRequiredService<GeminiClient>(),
            sp.GetRequiredService<ITtsProvider>(),
            sp.GetRequiredService<ISettingsRepository>(),
            sp.GetRequiredService<IMetricsRepository>(),
            sp.GetRequiredService<EarwormConfig>(),
            sp.GetRequiredService<ILogger<DJEngine>>(),
            gid)));

        services.AddSingleton(sp => new PerGuildRegistry<PlayerEngine>(gid =>
        {
            var engine = new PlayerEngine(
                sp.GetRequiredService<IAudioService>(),
                sp.GetRequiredService<PerGuildRegistry<QueueManager>>().GetOrCreate(gid),
                sp.GetRequiredService<IQueueRepository>(),
                sp.GetRequiredService<IHistoryRepository>(),
                sp.GetRequiredService<IMetricsRepository>(),
                sp.GetRequiredService<PerGuildRegistry<AudioTransitionController>>().GetOrCreate(gid),
                sp.GetRequiredService<EarwormConfig>(),
                sp.GetRequiredService<ILogger<PlayerEngine>>(),
                sp.GetRequiredService<ShutdownLifetime>(),
                gid);
            engine.SetPreTrackHook(sp.GetRequiredService<PerGuildRegistry<DJEngine>>().GetOrCreate(gid).MaybePlayCommentaryAsync);
            return engine;
        }));

        services.AddSingleton<TrackQueuingService>();

        services.AddSingleton<VoiceManager>();
        services.AddSingleton<DiscordGateway>();
        services.AddSingleton<TenantLifecycleListener>();
        services.AddSingleton<MessageListener>();
        services.AddSingleton<NowPlayingPoster>();
        services.AddSingleton<TrackFailureHandler>();

        services.AddSingleton<PlaybackCommands>();
        services.AddSingleton<QueueCommands>();
        services.AddSingleton<InfoCommands>();
        services.AddSingleton<DJCommands>();
        services.AddSingleton<ConfigCommands>();
        services.AddSingleton<AdminCommands>();

        services.AddSingleton<HealthEndpoint>();
    }

    /// <summary>
    /// Starts the Lavalink audio service, retrying with exponential backoff until
    /// the server responds or shutdown is requested. Prevents the bot from exiting
    /// immediately when the Lavalink container is slower to start (e.g. Docker
    /// compose) and avoids flooding the server with reconnection attempts.
    /// </summary>
    private static async Task StartLavalinkWithBackoffAsync(
        IAudioService audioService,
        ShutdownLifetime shutdownLifetime,
        ILogger logger)
    {
        var initialDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(30);
        var delay = initialDelay;
        var attempt = 1;

        while (true)
        {
            shutdownLifetime.Token.ThrowIfCancellationRequested();

            try
            {
                logger.LogInformation("Connecting to Lavalink (attempt {Attempt})...", attempt);
                var startTask = audioService.StartAsync().AsTask();
                try
                {
                    await startTask.WaitAsync(shutdownLifetime.Token);
                }
                catch (OperationCanceledException) when (shutdownLifetime.Token.IsCancellationRequested)
                {
                    _ = ObserveCancelledLavalinkStartAsync(startTask, audioService, logger);
                    throw;
                }
                logger.LogInformation("Lavalink audio service started.");
                return;
            }
            catch (OperationCanceledException) when (shutdownLifetime.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to connect to Lavalink on attempt {Attempt}. Retrying in {DelayMs}ms...",
                    attempt,
                    delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, shutdownLifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
                attempt++;
            }
        }
    }

    private static async Task ObserveCancelledLavalinkStartAsync(
        Task startTask,
        IAudioService audioService,
        ILogger logger)
    {
        try
        {
            await startTask;
            await audioService.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Canceled Lavalink startup completed with an expected late failure.");
        }
    }

    /// <summary>
    /// One-time multi-tenant backfill run after migration 004: rewrites the
    /// sentinel '' guild_id rows the migration left behind to the configured
    /// Discord.GuildId, seeds the legacy tenant row, and seeds the now-playing
    /// channel setting from YAML. Idempotent — re-runs are no-ops.
    /// </summary>
    private static async Task BackfillLegacyTenantAsync(string connectionString, EarwormConfig config)
    {
        var guildId = DiscordGuildId.Normalize(config.Discord.GuildId, nameof(config.Discord.GuildId));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await conn.OpenAsync();

        // This is a migration bridge, not a permanent admission mechanism. Seed
        // only a database that still contains the sentinel rows created by
        // migration 004 (fresh databases contain them too). Once the sentinel
        // is gone, even an empty tenants table must not cause YAML to silently
        // re-admit a guild.
        bool hasLegacySentinel;
        using (var sentinelCmd = conn.CreateCommand())
        {
            sentinelCmd.CommandText = @"
                SELECT EXISTS(SELECT 1 FROM playback_state   WHERE guild_id = '') OR
                       EXISTS(SELECT 1 FROM snapshot         WHERE guild_id = '') OR
                       EXISTS(SELECT 1 FROM settings         WHERE guild_id = '') OR
                       EXISTS(SELECT 1 FROM metrics_global   WHERE guild_id = '') OR
                       EXISTS(SELECT 1 FROM metrics_per_user WHERE guild_id = '');
            ";
            hasLegacySentinel = Convert.ToInt64(await sentinelCmd.ExecuteScalarAsync()) != 0;
        }

        if (!hasLegacySentinel)
        {
            return;
        }

        // All three steps run as one atomic unit. Each is individually idempotent
        // (so a re-run after a crash still converges), but the transaction means no
        // other connection ever observes a half-applied backfill.
        using var transaction = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    UPDATE playback_state   SET guild_id = $g WHERE guild_id = '';
                    UPDATE snapshot         SET guild_id = $g WHERE guild_id = '';
                    UPDATE settings         SET guild_id = $g WHERE guild_id = '';
                    UPDATE metrics_global   SET guild_id = $g WHERE guild_id = '';
                    UPDATE metrics_per_user SET guild_id = $g WHERE guild_id = '';
                ";
                cmd.Parameters.AddWithValue("$g", guildId);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO tenants (guild_id, plan, status, created_at)
                    VALUES ($g, 'free', 'active', $now);
                ";
                cmd.Parameters.AddWithValue("$g", guildId);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync();
            }

            if (!string.IsNullOrWhiteSpace(config.Discord.NowPlayingChannelId))
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO settings (guild_id, key, value, updated_at)
                    VALUES ($g, 'now_playing_channel_id', $value, $now);
                ";
                cmd.Parameters.AddWithValue("$g", guildId);
                cmd.Parameters.AddWithValue("$value", config.Discord.NowPlayingChannelId);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void ValidateConfig(EarwormConfig config)
    {
        var botToken = Environment.GetEnvironmentVariable("EARWORM_DISCORD_BOT_TOKEN");
        var geminiKey = Environment.GetEnvironmentVariable("EARWORM_GEMINI_API_KEY");
        var elevenlabsKey = Environment.GetEnvironmentVariable("EARWORM_ELEVENLABS_API_KEY");

        if (string.IsNullOrWhiteSpace(botToken))
            throw new InvalidOperationException("EARWORM_DISCORD_BOT_TOKEN environment variable is missing.");
        if (string.IsNullOrWhiteSpace(geminiKey))
            throw new InvalidOperationException("EARWORM_GEMINI_API_KEY environment variable is missing.");
        if (string.IsNullOrWhiteSpace(elevenlabsKey))
            throw new InvalidOperationException("EARWORM_ELEVENLABS_API_KEY environment variable is missing.");

        if (string.IsNullOrWhiteSpace(config.Discord.GuildId) || config.Discord.GuildId.Contains("REQUIRED"))
            throw new InvalidOperationException("discord.guild_id is required in conf/earworm.yaml.");
        // Must be a real snowflake: a non-numeric value passes the check above but
        // then fails ulong.TryParse downstream, which silently skips the tenant
        // backfill AND registers commands for zero guilds — the bot comes up but
        // admits and serves nobody. Fail fast instead.
        if (!DiscordGuildId.TryNormalize(config.Discord.GuildId, out _))
            throw new InvalidOperationException($"discord.guild_id must be a numeric Discord snowflake; got '{config.Discord.GuildId}'.");

        foreach (var ownerUserId in config.Bot.OwnerUserIds)
        {
            if (!ulong.TryParse(ownerUserId?.Trim(), out var parsedOwnerId) || parsedOwnerId == 0)
                throw new InvalidOperationException($"Bot.OwnerUserIds contains an invalid Discord user ID: '{ownerUserId}'.");
        }

        if (string.IsNullOrWhiteSpace(config.Dj.Tts.VoiceId) || config.Dj.Tts.VoiceId.Contains("REQUIRED"))
            throw new InvalidOperationException("dj.tts.voice_id is required in conf/earworm.yaml.");

        if (string.IsNullOrWhiteSpace(config.Lavalink.Host))
            throw new InvalidOperationException("lavalink.host is required in conf/earworm.yaml.");
        if (string.IsNullOrWhiteSpace(config.Lavalink.Password))
            throw new InvalidOperationException("lavalink.password is required in conf/earworm.yaml.");
    }

    private static void TryLoadDotEnv()
    {
        var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (!File.Exists(envPath)) return;

        try
        {
            DotNetEnv.Env.Load(envPath, new DotNetEnv.LoadOptions(
                setEnvVars: true,
                clobberExistingVars: false,
                onlyExactPath: true));
            Console.WriteLine($"Loaded environment overrides from {envPath} (existing env vars preserved).");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: failed to load .env at {envPath}: {ex.Message}");
            Console.ResetColor();
        }
    }
}
