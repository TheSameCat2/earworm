using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lavalink4NET;
using Lavalink4NET.Rest.Entities.Tracks;
using Earworm.Persistence.Repositories;

namespace Earworm.Domain.Queue;

/// <summary>
/// "User wants this URL/query queued" — used by both /play and @mention.
/// Resolves the track via Lavalink (one round-trip to the Lavalink REST API
/// gives us title/artist/duration/source-type), then writes to QueueManager
/// and increments per-source metrics. PlayerEngine reads from QueueManager
/// when the current track ends.
/// </summary>
public sealed class TrackQueuingService
{
    private readonly IAudioService _audioService;
    private readonly QueueManager _queueManager;
    private readonly IMetricsRepository _metrics;
    private readonly ILogger<TrackQueuingService> _logger;

    public TrackQueuingService(
        IAudioService audioService,
        QueueManager queueManager,
        IMetricsRepository metrics,
        ILogger<TrackQueuingService> logger)
    {
        _audioService = audioService;
        _queueManager = queueManager;
        _metrics = metrics;
        _logger = logger;
    }

    public bool IsPlaylistUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var q = query.Trim();
        // YouTube playlist links carry list= or are a /playlist? URL.
        // SoundCloud "/sets/" indicates a set.
        return q.Contains("list=", StringComparison.OrdinalIgnoreCase)
            || q.Contains("/playlist", StringComparison.OrdinalIgnoreCase)
            || q.Contains("/sets/", StringComparison.OrdinalIgnoreCase);
    }

    public bool LooksLikeUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var s = query.Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve via Lavalink, add to queue, increment per-source-type metrics.
    /// </summary>
    public async Task<QueueItem> ResolveAndQueueAsync(
        string query,
        string userId,
        string displayName,
        string guildId)
    {
        bool wasUrl = LooksLikeUrl(query);
        var searchMode = wasUrl ? TrackSearchMode.None : TrackSearchMode.YouTube;

        var track = await _audioService.Tracks.LoadTrackAsync(query, searchMode);
        if (track == null)
        {
            throw new InvalidOperationException($"Couldn't resolve a track from '{query}'.");
        }

        // Map Lavalink source name → our internal source_type.
        string sourceType = (track.SourceName ?? "").ToLowerInvariant() switch
        {
            "youtube" => "youtube",
            "soundcloud" => "soundcloud",
            "http" => "mp3_upload",  // Discord attachment URLs come through "http"
            _ => track.SourceName?.ToLowerInvariant() ?? "unknown"
        };

        // SourceId: prefer Lavalink's identifier (video id, soundcloud id), but
        // fall back to the full URI for raw http sources. PlayerEngine
        // reconstructs YouTube URLs from the id when playing.
        string sourceId = !string.IsNullOrEmpty(track.Identifier)
            ? track.Identifier
            : (track.Uri?.ToString() ?? query);

        var title = string.IsNullOrWhiteSpace(track.Title) ? "Unknown Title" : track.Title!;
        var author = string.IsNullOrWhiteSpace(track.Author) ? "Unknown Artist" : track.Author!;
        int? durationSec = track.Duration.TotalSeconds > 0 ? (int)track.Duration.TotalSeconds : null;

        await _queueManager.AddTrackAsync(
            sourceType: sourceType,
            sourceId: sourceId,
            title: title,
            artist: author,
            durationSeconds: durationSec,
            requestedByUserId: userId,
            requestedByDisplayName: displayName,
            guildId: guildId);

        // Per-source metric bucket.
        string bucket = wasUrl
            ? sourceType switch
            {
                "youtube" => "requests_youtube",
                "soundcloud" => "requests_soundcloud",
                "mp3_upload" => "requests_mp3_upload",
                _ => "requests_youtube"
            }
            : "requests_search";

        try
        {
            await _metrics.IncrementBatchAsync(new[]
            {
                new MetricIncrement(bucket, 1, userId, displayName),
                new MetricIncrement("tracks_queued", 1, userId, displayName),
                new MetricIncrement("tracks_queued", 1),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to increment queue metrics for user {UserId}.", userId);
        }

        return new QueueItem
        {
            SourceType = sourceType,
            SourceId = sourceId,
            Title = title,
            Artist = author,
            DurationSeconds = durationSec,
            RequestedByUserId = userId,
            RequestedByDisplayName = displayName,
            GuildId = guildId
        };
    }
}
