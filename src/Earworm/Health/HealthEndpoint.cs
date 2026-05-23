using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Earworm.Config;
using Earworm.Discord;

namespace Earworm.Health;

/// <summary>
/// In-process ASP.NET Core minimal-API host providing the ops endpoints required
/// by PRD §11:
///
///   GET /health   → 200 OK when the Discord gateway WebSocket is ready, else 503.
///                   Wired into the Dockerfile HEALTHCHECK.
///   GET /metrics  → Prometheus exposition (currently a scaffold — returns 503 or a
///                   "metrics disabled" body unless EARWORM_METRICS_ENABLED=true and
///                   the exporter is wired up).
///
/// Liveness depth is set deliberately at "gateway connected": shallower checks
/// (process alive) miss zombie states; deeper (voice pipeline functional) flap
/// during normal voice reconnects.
/// </summary>
public sealed class HealthEndpoint : IAsyncDisposable
{
    private readonly EarwormConfig _config;
    private readonly DiscordGateway _gateway;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HealthEndpoint> _logger;
    private WebApplication? _app;

    public HealthEndpoint(
        EarwormConfig config,
        DiscordGateway gateway,
        ILoggerFactory loggerFactory,
        ILogger<HealthEndpoint> logger)
    {
        _config = config;
        _gateway = gateway;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        int port = _config.Ops.HttpPort;
        _logger.LogInformation("Starting in-process HTTP host on port {Port} for /health (+ scaffolded /metrics).", port);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // Reuse the existing logger factory so health-host logs land in the same JSON stream as the bot.
        builder.Services.AddSingleton(_loggerFactory);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();

        app.MapGet("/health", () =>
        {
            return _gateway.IsReady
                ? Results.Ok(new { status = "ok" })
                : Results.Json(new { status = "starting" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // PRD §12 (Lavalink-edition): Lavalink fetches staged TTS audio from
        // this endpoint. Only serves files in the configured TTS scratch dir
        // and rejects anything that smells like a path-traversal attempt.
        app.MapGet("/tts/{file}", (string file) =>
        {
            // Allowlist filename: 32-hex (Guid "N") + .mp3 — exactly what
            // DJEngine writes. Anything else is a probe; 404 silently.
            if (!Regex.IsMatch(file, @"^[a-f0-9]{32}\.mp3$"))
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
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
