using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lavalink4NET;
using Earworm.Config;
using Earworm.Discord;
using Earworm.Domain.Tenants;
using Earworm.Persistence;

namespace Earworm.Health;

/// <summary>
/// In-process ASP.NET Core minimal-API host providing the ops endpoints required
/// by PRD §11:
///
///   GET /live     → 200 OK while this process can serve HTTP. Used by the
///                   Docker HEALTHCHECK so an upstream outage does not cause a
///                   restart loop.
///   GET /health   → 200 OK when Discord, Lavalink, SQLite's writer, and the
///                   tenant store are ready, else 503. Existing response fields
///                   remain stable; additional dependency details are additive.
///   GET /metrics  → Prometheus exposition (currently a scaffold — returns 503 or a
///                   "metrics disabled" body unless EARWORM_METRICS_ENABLED=true and
///                   the exporter is wired up).
///
/// Readiness is deliberately deeper than liveness: dependency failures are
/// visible to monitoring without turning a recoverable outage into a restart
/// loop. Discord zombie/close events clear the gateway readiness bit.
/// </summary>
public sealed partial class HealthEndpoint : IAsyncDisposable
{
    private readonly EarwormConfig _config;
    private readonly DiscordGateway _gateway;
    private readonly IAudioService _audioService;
    private readonly ITenantService _tenants;
    private readonly StateStore _stateStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HealthEndpoint> _logger;
    private WebApplication? _app;

    public HealthEndpoint(
        EarwormConfig config,
        DiscordGateway gateway,
        IAudioService audioService,
        ITenantService tenants,
        StateStore stateStore,
        ILoggerFactory loggerFactory,
        ILogger<HealthEndpoint> logger)
    {
        _config = config;
        _gateway = gateway;
        _audioService = audioService;
        _tenants = tenants;
        _stateStore = stateStore;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    [GeneratedRegex(@"^[a-f0-9]{32}\.mp3$")]
    private static partial Regex TtsFilenamePattern();

    public async Task StartAsync()
    {
        int port = _config.Ops.HttpPort;
        _logger.LogInformation("Starting in-process HTTP host on port {Port} for /live, /health, and /metrics.", port);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // Reuse the existing logger factory so health-host logs land in the same JSON stream as the bot.
        builder.Services.AddSingleton(_loggerFactory);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        // Bound the host-internal shutdown wait so a slow in-flight /tts stream
        // can't push past Docker's 10s SIGTERM grace and trigger SIGKILL mid-shutdown.
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(5));

        var app = builder.Build();

        // Liveness is deliberately shallow: if the HTTP loop can answer, the
        // process is alive. Readiness belongs on /health so Discord/Lavalink or
        // SQLite outages do not make Docker repeatedly restart a healthy process.
        app.MapGet("/live", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health", async () =>
        {
            bool discordReady = _gateway.IsReady;
            // IAudioService exposes only WaitForReadyAsync; the underlying TCS
            // completes synchronously when ready, so IsCompletedSuccessfully on
            // the returned ValueTask is a non-blocking readiness probe.
            bool lavalinkReady = _audioService.WaitForReadyAsync(CancellationToken.None).IsCompletedSuccessfully;

            string discordStatus = discordReady ? "ok" : "starting";
            string lavalinkStatus = lavalinkReady ? "ok" : "down";
            bool writerReady = _stateStore.IsWriterHealthy;
            string writerStatus = writerReady ? "ok" : "down";
            int pendingWrites = _stateStore.PendingWriteCount;

            // Admitted tenants per the whitelist table (status 'active') — the
            // authoritative count. A query failure now flips readiness (but not
            // /live), because serving tenants without access to the whitelist is
            // not a healthy service state.
            int activeTenants;
            bool tenantStoreReady;
            try
            {
                var tenants = await _tenants.GetAllTenantsAsync();
                activeTenants = tenants.Count(t => string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase));
                tenantStoreReady = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read tenant count for /health.");
                activeTenants = -1;
                tenantStoreReady = false;
            }

            string tenantStoreStatus = tenantStoreReady ? "ok" : "down";

            if (discordReady && lavalinkReady && writerReady && tenantStoreReady)
            {
                return Results.Ok(new
                {
                    status = "ok",
                    discord = discordStatus,
                    lavalink = lavalinkStatus,
                    tenants = activeTenants,
                    tenantStore = tenantStoreStatus,
                    writer = writerStatus,
                    pendingWrites
                });
            }

            string overall = discordReady ? "degraded" : "starting";
            return Results.Json(
                new
                {
                    status = overall,
                    discord = discordStatus,
                    lavalink = lavalinkStatus,
                    tenants = activeTenants,
                    tenantStore = tenantStoreStatus,
                    writer = writerStatus,
                    pendingWrites
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // PRD §12 (Lavalink-edition): Lavalink fetches staged TTS audio from
        // this endpoint. Only serves files in the configured TTS scratch dir
        // and rejects anything that smells like a path-traversal attempt.
        app.MapGet("/tts/{file}", (string file) =>
        {
            // Allowlist filename: 32-hex (Guid "N") + .mp3 — exactly what
            // DJEngine writes. Anything else is a probe; 404 silently.
            if (!TtsFilenamePattern().IsMatch(file))
            {
                return Results.NotFound();
            }

            var scratchDir = Path.GetFullPath(_config.Dj.TtsScratchDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(scratchDir, file));

            // Defense-in-depth: even though the regex already constrains the
            // filename, verify the resolved path stays under the scratch dir.
            if (!fullPath.StartsWith(scratchDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Results.NotFound();
            }

            if (!File.Exists(fullPath))
            {
                return Results.NotFound();
            }

            return Results.File(fullPath, "audio/mpeg", enableRangeProcessing: true);
        });

        app.MapGet("/metrics", () =>
        {
            bool enabled = string.Equals(
                Environment.GetEnvironmentVariable("EARWORM_METRICS_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            if (!enabled)
            {
                return Results.Text(
                    "# /metrics is disabled. Set EARWORM_METRICS_ENABLED=true to enable.\n",
                    "text/plain; version=0.0.4",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Scaffold — real Prometheus exposition lives behind a future MetricsRegistry.
            // PRD §11 calls /metrics "disabled by default" and ships only the toggle for v1.
            return Results.Text(
                "# HELP earworm_up 1 if the process is alive\n# TYPE earworm_up gauge\nearworm_up 1\n",
                "text/plain; version=0.0.4");
        });

        await app.StartAsync();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app != null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
