using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Earworm.Config;

namespace Earworm.Domain.DJ;

/// <summary>
/// Cleans up orphaned TTS .mp3 files in <see cref="DjConfig.TtsScratchDirectory"/>.
///
/// Two protection layers:
///   1. <see cref="SweepOnStartup"/> — runs once at process start and removes any
///      files left over from a previous crash before the first DJ render.
///   2. <see cref="StartPeriodicSweep"/> — background loop that fires every 5 minutes
///      and enforces both a maximum-age policy and a count cap.
/// </summary>
public sealed class TtsScratchJanitor
{
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(5);

    private readonly EarwormConfig _config;
    private readonly ILogger<TtsScratchJanitor> _logger;
    private readonly ShutdownLifetime _shutdown;

    public TtsScratchJanitor(
        EarwormConfig config,
        ILogger<TtsScratchJanitor> logger,
        ShutdownLifetime shutdown)
    {
        _config = config;
        _logger = logger;
        _shutdown = shutdown;
    }

    /// <summary>
    /// Deletes all existing *.mp3 files in the scratch directory. Call once at
    /// startup before any TTS files are rendered.
    /// </summary>
    public void SweepOnStartup()
    {
        if (_shutdown.IsShuttingDown) return;

        string dir = _config.Dj.TtsScratchDirectory;
        if (!Directory.Exists(dir))
        {
            _logger.LogDebug("TTS scratch directory {Dir} does not exist; nothing to sweep on startup.", dir);
            return;
        }

        var files = Directory.GetFiles(dir, "*.mp3");
        int deleted = 0;
        foreach (var file in files)
        {
            if (_shutdown.IsShuttingDown) break;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup sweep: failed to delete {File}.", file);
            }
        }

        _logger.LogInformation("TTS scratch startup sweep: deleted {Count} orphaned file(s) from {Dir}.", deleted, dir);
    }

    /// <summary>
    /// Starts a background loop that periodically enforces the age and count
    /// retention policies. The loop exits cleanly when the shutdown token fires.
    /// </summary>
    public void StartPeriodicSweep()
    {
        var token = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(PeriodicInterval, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;

                    try { RunRetentionPass(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TTS scratch retention pass encountered an error.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path — not an error.
            }
        }, token);
    }

    /// <summary>
    /// Runs the age-based and count-based pruning passes against the scratch directory.
    /// </summary>
    public void RunRetentionPass()
    {
        string dir = _config.Dj.TtsScratchDirectory;
        if (!Directory.Exists(dir)) return;

        var files = Directory.GetFiles(dir, "*.mp3")
            .Select(f => new FileInfo(f))
            .OrderBy(fi => fi.LastWriteTimeUtc)
            .ToList();

        if (files.Count == 0) return;

        // Minimum-age protection: a file younger than this is still likely being
        // streamed by Lavalink (staged but not yet consumed via OnConsumedAsync).
        // Never delete it from the retention pass — let the OnConsumedAsync
        // callback or a future pass handle it once it's safely older.
        var minAge = TimeSpan.FromMinutes(Math.Max(0, _config.Dj.TtsScratchMinAgeMinutes));
        var minAgeCutoff = DateTime.UtcNow - minAge;

        // Age-based pruning.
        var maxAge = TimeSpan.FromMinutes(Math.Max(1, _config.Dj.TtsScratchMaxAgeMinutes));
        var cutoff = DateTime.UtcNow - maxAge;
        int deletedAge = 0;
        foreach (var fi in files.ToList())
        {
            // Skip files still inside the minimum-age window (may be streaming).
            if (fi.LastWriteTimeUtc > minAgeCutoff) continue;

            if (fi.LastWriteTimeUtc < cutoff)
            {
                TryDelete(fi, "age");
                files.Remove(fi);
                deletedAge++;
            }
        }

        if (deletedAge > 0)
            _logger.LogInformation("TTS scratch age sweep: deleted {Count} file(s) older than {MaxAgeMinutes} min.", deletedAge, _config.Dj.TtsScratchMaxAgeMinutes);

        // Count-based pruning: oldest excess files are already first after OrderBy.
        // Skip any file younger than the minimum age — it may still be streaming.
        int maxFiles = Math.Max(1, _config.Dj.TtsScratchMaxFiles);
        if (files.Count > maxFiles)
        {
            int excess = files.Count - maxFiles;
            int deletedCount = 0;
            for (int i = 0; i < files.Count && deletedCount < excess; i++)
            {
                if (files[i].LastWriteTimeUtc > minAgeCutoff) continue; // protected
                TryDelete(files[i], "count cap");
                deletedCount++;
            }
            if (deletedCount > 0)
                _logger.LogInformation("TTS scratch count sweep: deleted {Count} excess file(s) (cap={Cap}).", deletedCount, maxFiles);
            if (deletedCount < excess)
                _logger.LogWarning("TTS scratch count sweep: {Protected} excess file(s) skipped because they are younger than {MinAgeMinutes} min and may still be streaming.", excess - deletedCount, _config.Dj.TtsScratchMinAgeMinutes);
        }
    }

    private void TryDelete(FileInfo fi, string reason)
    {
        try
        {
            fi.Delete();
            _logger.LogDebug("Deleted TTS scratch file {File} ({Reason}).", fi.Name, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete TTS scratch file {File} ({Reason}).", fi.Name, reason);
        }
    }
}
