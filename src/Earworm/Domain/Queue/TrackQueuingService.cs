using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lavalink4NET;
using Lavalink4NET.Rest.Entities.Tracks;
using Earworm.Config;
using Earworm.Infrastructure;
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
    private readonly PerGuildRegistry<QueueManager> _queueManagers;
    private readonly IMetricsRepository _metrics;
    private readonly ILogger<TrackQueuingService> _logger;
    private readonly SemaphoreSlim _globalResolutionLimit;
    private readonly ConcurrentDictionary<string, GuildResolutionGate> _guildResolutionLimits = new();
    private readonly int _perGuildResolutionLimit;
    private readonly int _pendingResolutionLimitPerGuild;

    private sealed class GuildResolutionGate
    {
        public GuildResolutionGate(int concurrency, int pending)
        {
            Concurrency = new SemaphoreSlim(concurrency, concurrency);
            long requestedCapacity = (long)concurrency + pending;
            int admissionCapacity = (int)Math.Min(int.MaxValue, requestedCapacity);
            Admission = new SemaphoreSlim(admissionCapacity, admissionCapacity);
        }

        public SemaphoreSlim Admission { get; }
        public SemaphoreSlim Concurrency { get; }
    }

    public TrackQueuingService(
        IAudioService audioService,
        PerGuildRegistry<QueueManager> queueManagers,
        IMetricsRepository metrics,
        EarwormConfig config,
        ILogger<TrackQueuingService> logger)
    {
        _audioService = audioService;
        _queueManagers = queueManagers;
        _metrics = metrics;
        _logger = logger;
        int globalLimit = Math.Max(1, config.Ops.MaxConcurrentTrackResolutions);
        _globalResolutionLimit = new SemaphoreSlim(globalLimit, globalLimit);

        int configuredGuildLimit = Math.Max(1, config.Ops.MaxConcurrentTrackResolutionsPerGuild);
        _perGuildResolutionLimit = globalLimit > 1
            ? Math.Min(configuredGuildLimit, globalLimit - 1)
            : 1;
        if (_perGuildResolutionLimit != configuredGuildLimit)
        {
            _logger.LogWarning(
                "Clamped per-guild track resolution concurrency from {Configured} to {Effective} for global limit {Global}.",
                configuredGuildLimit,
                _perGuildResolutionLimit,
                globalLimit);
        }

        _pendingResolutionLimitPerGuild = Math.Max(0, config.Ops.MaxPendingTrackResolutionsPerGuild);
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
        if (wasUrl)
        {
            ValidatePublicTrackUrl(query);
        }

        var searchMode = wasUrl ? TrackSearchMode.None : TrackSearchMode.YouTube;

        // A noisy guild should wait for a bounded share of the common Lavalink
        // resolver rather than fanning out unbounded work for every mention.
        var guildGate = _guildResolutionLimits.GetOrAdd(
            guildId,
            _ => new GuildResolutionGate(_perGuildResolutionLimit, _pendingResolutionLimitPerGuild));
        if (!guildGate.Admission.Wait(0))
        {
            throw new InvalidOperationException(
                $"Too many track requests are already pending for this server (limit: {_pendingResolutionLimitPerGuild}). Try again shortly.");
        }

        // Claim this guild's share before a process-wide slot. Otherwise one
        // noisy guild can fill every global permit with requests that are only
        // waiting for its smaller per-guild gate, starving all other tenants.
        Lavalink4NET.Tracks.LavalinkTrack? track;
        try
        {
            await guildGate.Concurrency.WaitAsync();
            try
            {
                await _globalResolutionLimit.WaitAsync();
                try
                {
                    track = await _audioService.Tracks.LoadTrackAsync(query, searchMode);
                }
                finally
                {
                    _globalResolutionLimit.Release();
                }
            }
            finally
            {
                guildGate.Concurrency.Release();
            }
        }
        finally
        {
            guildGate.Admission.Release();
        }
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

        var queued = await _queueManagers.GetOrCreate(guildId).AddTrackAsync(
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
            await _metrics.IncrementBatchAsync(guildId, new[]
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

        return queued;
    }

    /// <summary>
    /// Blocks the direct internal-network URL forms that would otherwise turn
    /// Lavalink's HTTP source into an SSRF primitive. Public HTTP audio remains
    /// supported for backward compatibility. Network-level egress policy should
    /// still be used because DNS can change between validation and fetch.
    /// </summary>
    internal static void ValidatePublicTrackUrl(string query)
    {
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Track URLs must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Track URLs containing embedded credentials are not allowed.");
        }

        string host = uri.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out var address))
        {
            if (!IsPublicAddress(address))
            {
                throw new InvalidOperationException("Private or local track URLs are not allowed.");
            }
            return;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.') ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Private or local track URLs are not allowed.");
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 0 &&
                   bytes[0] != 10 &&
                   bytes[0] != 127 &&
                   !(bytes[0] == 169 && bytes[1] == 254) &&
                   !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                   !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                   !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) &&
                   !(bytes[0] == 192 && bytes[1] == 168) &&
                   !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
                   bytes[0] < 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !address.IsIPv6LinkLocal &&
                   !address.IsIPv6Multicast &&
                   !address.IsIPv6SiteLocal &&
                   (bytes[0] & 0xFE) != 0xFC; // fc00::/7 unique-local
        }

        return false;
    }
}
