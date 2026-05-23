using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Earworm.Config;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Persistence.Repositories;

namespace Earworm.Domain.DJ;

/// <summary>
/// Gemini-powered DJ commentary (PRD §12). Pause-insert-resume model in the
/// Lavalink era:
///   1. Cadence rolls (1..MaxGapTracks).
///   2. Generate commentary text via Gemini.
///   3. Render to MP3 via ElevenLabs.
///   4. Save MP3 to <see cref="DjConfig.TtsScratchDirectory"/>.
///   5. Return a <see cref="TtsPreroll"/> with an HTTP URL Lavalink can fetch.
///   6. PlayerEngine plays the TTS via Lavalink, then plays the music track.
///   7. After playback, PlayerEngine invokes <c>OnConsumedAsync</c> which
///      deletes the staged file.
///
/// True ducking (TTS mixed over continuing music) isn't possible with a
/// single LavalinkPlayer per voice connection. The "ducking" feel is faked
/// by <see cref="Player.AudioTransitionController"/> ramping the music
/// volume down before the preroll and the next track's volume up after.
/// </summary>
public sealed class DJEngine : IDisposable
{
    private readonly PlayerEngine _playerEngine;
    private readonly GeminiClient _geminiClient;
    private readonly ITtsProvider _ttsProvider;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IMetricsRepository _metricsRepository;
    private readonly EarwormConfig _config;
    private readonly ILogger<DJEngine> _logger;

    private readonly Random _random = new();
    private readonly object _stateLock = new();
    private int _tracksSinceCommentary;
    private int _targetGap;

    public DJEngine(
        PlayerEngine playerEngine,
        GeminiClient geminiClient,
        ITtsProvider ttsProvider,
        ISettingsRepository settingsRepository,
        IMetricsRepository metricsRepository,
        EarwormConfig config,
        ILogger<DJEngine> logger)
    {
        _playerEngine = playerEngine;
        _geminiClient = geminiClient;
        _ttsProvider = ttsProvider;
        _settingsRepository = settingsRepository;
        _metricsRepository = metricsRepository;
        _config = config;
        _logger = logger;

        ResetTargetGap();
    }

    private void ResetTargetGap()
    {
        int maxGap = Math.Max(1, _config.Dj.MaxGapTracks);
        lock (_stateLock) { _targetGap = _random.Next(1, maxGap + 1); }
    }

    public async Task<TtsPreroll?> MaybePlayCommentaryAsync(QueueItem upcomingTrack, CancellationToken cancellationToken)
    {
        bool enabled;
        try { enabled = await _settingsRepository.IsDjEnabledAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read DJ-enabled setting; skipping commentary.");
            return null;
        }
        if (!enabled) return null;

        if (string.IsNullOrWhiteSpace(_config.Dj.TtsServeBaseUrl))
        {
            _logger.LogWarning("DJ enabled but Dj.TtsServeBaseUrl is empty — Lavalink has no URL to fetch TTS from. Skipping.");
            return null;
        }

        int sinceLast, target;
        lock (_stateLock)
        {
            _tracksSinceCommentary++;
            sinceLast = _tracksSinceCommentary;
            target = _targetGap;
        }
        if (sinceLast < target) return null;

        _logger.LogInformation("DJ cadence reached ({Since}/{Target}); generating commentary for: {Title}",
            sinceLast, target, upcomingTrack.Title);

        // Reset cadence counters BEFORE the slow path so we don't double-fire
        // on a parallel queue advance.
        lock (_stateLock) { _tracksSinceCommentary = 0; }
        ResetTargetGap();

        string? scratchPath = null;
        try
        {
            string text = await _geminiClient.GenerateCommentaryAsync(
                upcomingTrack.Title ?? "Unknown Title",
                upcomingTrack.Artist ?? "Unknown Artist",
                cancellationToken);

            // PRD §12 hard cap: a runaway generation can't blow the 10-15s
            // audio cap if the text itself is bounded.
            const int hardCharCap = 320;
            if (text.Length > hardCharCap)
            {
                text = text.Substring(0, hardCharCap).TrimEnd() + "...";
            }

            // Render to MP3 and stage on disk.
            string scratchDir = _config.Dj.TtsScratchDirectory;
            Directory.CreateDirectory(scratchDir);
            string filename = $"{Guid.NewGuid():N}.mp3";
            scratchPath = Path.Combine(scratchDir, filename);

            using (var ttsStream = await _ttsProvider.RenderTtsAsync(text, cancellationToken))
            using (var fileStream = File.Create(scratchPath))
            {
                await ttsStream.CopyToAsync(fileStream, cancellationToken);
            }

            // PRD §11: best-effort metric increment.
            try { await _metricsRepository.IncrementGlobalMetricAsync("dj_commentary_count", 1); }
            catch { /* best-effort */ }

            string baseUrl = _config.Dj.TtsServeBaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/tts/{filename}";

            // Capture the path locally so the cleanup closure doesn't race
            // with subsequent renders that overwrite scratchPath.
            string pathForCleanup = scratchPath;
            Func<Task> cleanup = () =>
            {
                try
                {
                    if (File.Exists(pathForCleanup)) File.Delete(pathForCleanup);
                    _logger.LogDebug("Cleaned up staged TTS file {Path}.", pathForCleanup);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete staged TTS file {Path}.", pathForCleanup);
                }
                return Task.CompletedTask;
            };

            _logger.LogInformation("Staged DJ commentary at {Url} ({Bytes} bytes on disk).",
                url, new FileInfo(scratchPath).Length);

            return new TtsPreroll(url, cleanup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate or stage DJ commentary; skipping for this track.");

            // Best-effort delete if we got partway through.
            if (scratchPath != null && File.Exists(scratchPath))
            {
                try { File.Delete(scratchPath); } catch { /* ignore */ }
            }

            return null;
        }
    }

    public void Dispose() { }
}
