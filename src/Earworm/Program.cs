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
            Environment.Exit(1);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Configuration loaded and verified successfully.");
        Console.ResetColor();

        var botToken = Environment.GetEnvironmentVariable("EARWORM_DISCORD_BOT_TOKEN") ?? "PLACEHOLDER";

        var services = new ServiceCollection();
        ConfigureServices(services, earwormConfig, botToken);

        // await using ensures all IDisposable / IAsyncDisposable singletons are
        // torn down on Main exit — most importantly StateStore, which drains
        // the SQLite write channel and checkpoints WAL on Dispose.
        await using var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<object>>();
        var shutdownLifetime = serviceProvider.GetRequiredService<ShutdownLifetime>();

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

            if (ulong.TryParse(earwormConfig.Discord.GuildId, out _))
            {
                await BackfillLegacyTenantAsync(stateStore.ConnectionString, earwormConfig);
            }
            else
            {
                logger.LogWarning("Skipped tenant backfill: Discord.GuildId '{GuildId}' is not a valid numeric snowflake.", earwormConfig.Discord.GuildId);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed. Shutting down.");
            Environment.Exit(1);
        }

        // Slash commands. UseSlashCommands accepts a ServiceProvider; command
        // classes are resolved from it per-invocation. Registration is per active
        // tenant (instant propagation) via TenantLifecycleListener.
        var slash = client.UseSlashCommands(new SlashCommandsConfiguration { Services = serviceProvider });

        var lifecycle = serviceProvider.GetRequiredService<TenantLifecycleListener>();
        lifecycle.Attach(slash);
        await lifecycle.RegisterStartupCommandsAsync();

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
            Environment.Exit(1);
        }

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
            Environment.Exit(1);
        }

        // Discord gateway connect.
        var gateway = serviceProvider.GetRequiredService<DiscordGateway>();
        try
        {
            await gateway.StartAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to start Discord Gateway. Shutting down.");
            Environment.Exit(1);
        }

        // Lavalink connect. Must come AFTER Discord client is connected so
        // Lavalink4NET can read currentUser.Id for its WebSocket handshake.
        var audioService = serviceProvider.GetRequiredService<IAudioService>();
        try
        {
            await audioService.StartAsync();
            logger.LogInformation("Lavalink audio service started.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to start Lavalink audio service. Is the Lavalink server running and reachable?");
            Environment.Exit(1);
        }

        logger.LogInformation("earworm services started. Press Ctrl+C to shut down.");

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Shutdown requested (SIGINT).");
            shutdownLifetime.Cancel();
            tcs.TrySetResult();
        };
        // Docker `stop` sends SIGTERM, not SIGINT. Without this handler the
        // runtime exits immediately, skipping the shutdown block below — which
        // leaves SQLite WAL un-checkpointed and the Discord/Lavalink sessions
        // dangling until container kill.
        using var sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            logger.LogInformation("Shutdown requested (SIGTERM).");
            shutdownLifetime.Cancel();
            tcs.TrySetResult();
        });

        await tcs.Task;

        logger.LogInformation("Shutting down...");

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
    /// One-time multi-tenant backfill run after migration 004: rewrites the
    /// sentinel '' guild_id rows the migration left behind to the configured
    /// Discord.GuildId, seeds the legacy tenant row, and seeds the now-playing
    /// channel setting from YAML. Idempotent — re-runs are no-ops.
    /// </summary>
    private static async Task BackfillLegacyTenantAsync(string connectionString, EarwormConfig config)
    {
        var guildId = config.Discord.GuildId;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await conn.OpenAsync();

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
        if (!ulong.TryParse(config.Discord.GuildId, out _))
            throw new InvalidOperationException($"discord.guild_id must be a numeric Discord snowflake; got '{config.Discord.GuildId}'.");

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
