using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DSharpPlus;
using DSharpPlus.SlashCommands;
using Lavalink4NET;
using Lavalink4NET.Extensions;
using Lavalink4NET.InactivityTracking.Extensions;
using Earworm.Config;
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

        // Slash commands. UseSlashCommands accepts a ServiceProvider; command
        // classes are resolved from it per-invocation. Per-guild registration is
        // handled by TenantLifecycleListener.RegisterStartupCommandsAsync() below,
        // which queries active tenants and registers commands for each guild.
        client.UseSlashCommands(new SlashCommandsConfiguration { Services = serviceProvider });

        // Migrations.
        try
        {
            var stateStore = serviceProvider.GetRequiredService<StateStore>();
            var migrationLogger = serviceProvider.GetRequiredService<ILogger<SchemaMigrator>>();
            new SchemaMigrator(stateStore.ConnectionString, migrationLogger).Migrate();

            // Ensure singleton rows exist for databases created before the seed
            // statements were added to the migration, or restored from a backup
            // that is missing them.
            using var seedConn = new Microsoft.Data.Sqlite.SqliteConnection(stateStore.ConnectionString);
            await seedConn.OpenAsync();
            using (var seedCmd = seedConn.CreateCommand())
            {
                seedCmd.CommandText = @"
                    INSERT OR IGNORE INTO playback_state (id, is_playing, is_paused, current_position_ms, updated_at)
                        VALUES (1, 0, 0, 0, $now);
                    INSERT OR IGNORE INTO snapshot (id) VALUES (1);
                ";
                seedCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await seedCmd.ExecuteNonQueryAsync();
            }

            // Seed initial tenant row from config (one-time; idempotent)
            if (ulong.TryParse(earwormConfig.Discord.GuildId, out _))
            {
                using (var tenantSeedCmd = seedConn.CreateCommand())
                {
                    tenantSeedCmd.CommandText = @"
                        INSERT OR IGNORE INTO tenants (guild_id, plan, status, created_at)
                        VALUES ($guild_id, 'free', 'active', $now);
                    ";
                    tenantSeedCmd.Parameters.AddWithValue("$guild_id", earwormConfig.Discord.GuildId);
                    tenantSeedCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await tenantSeedCmd.ExecuteNonQueryAsync();
                }
            }
            else
            {
                logger.LogWarning("Skipped tenant seed: Discord.GuildId '{GuildId}' is not a valid numeric snowflake.", earwormConfig.Discord.GuildId);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed. Shutting down.");
            Environment.Exit(1);
        }

        try
        {
            await serviceProvider.GetRequiredService<QueueManager>().InitializeAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Queue initialization failed. Shutting down.");
            Environment.Exit(1);
        }

        // Wire DJ commentary hook into PlayerEngine (avoids ctor-cycle DI).
        var djEngine = serviceProvider.GetRequiredService<DJEngine>();
        var playerEngine = serviceProvider.GetRequiredService<PlayerEngine>();
        playerEngine.SetPreTrackHook(djEngine.MaybePlayCommentaryAsync);

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

        // Eagerly resolve event-handler singletons so their ctor-time gateway
        // event subscriptions are wired before SessionCreated fires.
        _ = serviceProvider.GetRequiredService<MessageListener>();
        _ = serviceProvider.GetRequiredService<NowPlayingPoster>();
        _ = serviceProvider.GetRequiredService<VoiceManager>();
        _ = serviceProvider.GetRequiredService<TrackFailureHandler>();

        // Register slash commands per active tenant before connecting so DSharpPlus
        // pushes all registrations when the Ready event fires.
        var tenantLifecycleListener = serviceProvider.GetRequiredService<TenantLifecycleListener>();
        try
        {
            await tenantLifecycleListener.RegisterStartupCommandsAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to register startup slash commands. Shutting down.");
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

        services.AddSingleton<QueueManager>();
        services.AddSingleton<TrackQueuingService>();
        services.AddSingleton<AudioTransitionController>();
        services.AddSingleton<PlayerEngine>();

        // Per-call timeouts. Without these, a hung external API blocks the
        // caller for the OS-default ~100s TCP timeout, which during DJ commentary
        // generation freezes PlayNextAsync and stalls music playback.
        services.AddHttpClient(nameof(GeminiClient), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(nameof(ElevenLabsTtsProvider), c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<GeminiClient>();
        services.AddSingleton<ElevenLabsTtsProvider>();
        services.AddSingleton<ITtsProvider>(sp => sp.GetRequiredService<ElevenLabsTtsProvider>());
        services.AddSingleton<DJEngine>();
        services.AddSingleton<TtsScratchJanitor>();

        services.AddSingleton<VoiceManager>();
        services.AddSingleton<DiscordGateway>();
        services.AddSingleton<MessageListener>();
        services.AddSingleton<NowPlayingPoster>();
        services.AddSingleton<TrackFailureHandler>();
        services.AddSingleton<TenantLifecycleListener>();

        services.AddSingleton<PlaybackCommands>();
        services.AddSingleton<QueueCommands>();
        services.AddSingleton<InfoCommands>();
        services.AddSingleton<DJCommands>();
        services.AddSingleton<ConfigCommands>();
        services.AddSingleton<AdminCommands>();

        services.AddSingleton<HealthEndpoint>();
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
