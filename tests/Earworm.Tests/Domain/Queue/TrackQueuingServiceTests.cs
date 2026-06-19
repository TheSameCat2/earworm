using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Lavalink4NET;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Earworm.Config;
using Earworm.Domain.Queue;
using Earworm.Infrastructure;
using Earworm.Persistence.Repositories;

namespace Earworm.Tests.Domain.Queue;

public sealed class TrackQueuingServiceTests
{
    private static EarwormConfig BuildConfig() => new()
    {
        Discord = new DiscordConfig { GuildId = "1" },
    };

    private static TrackQueuingService BuildService(
        out IAudioService audioService,
        out PerGuildRegistry<QueueManager> queueRegistry,
        out IMetricsRepository metrics)
    {
        audioService = Substitute.For<IAudioService>();
        var trackManager = Substitute.For<ITrackManager>();
        audioService.Tracks.Returns(trackManager);

        var config = BuildConfig();
        var queueRepo = Substitute.For<IQueueRepository>();
        queueRepo.GetQueueAsync(Arg.Any<string>()).Returns(new System.Collections.Generic.List<QueueItem>());
        var snapshotRepo = Substitute.For<ISnapshotRepository>();
        var queueManager = new QueueManager(queueRepo, snapshotRepo, config,
            NullLogger<QueueManager>.Instance, "1");
        queueRegistry = new PerGuildRegistry<QueueManager>(_ => queueManager);

        metrics = Substitute.For<IMetricsRepository>();

        return new TrackQueuingService(
            audioService, queueRegistry, metrics,
            NullLogger<TrackQueuingService>.Instance);
    }

    [Fact]
    public void IsPlaylistUrl_DetectsYouTubePlaylist()
    {
        var svc = BuildService(out _, out _, out _);
        svc.IsPlaylistUrl("https://youtube.com/playlist?list=PLabc123").Should().BeTrue();
        svc.IsPlaylistUrl("https://youtube.com/watch?v=x&list=PLabc123").Should().BeTrue();
    }

    [Fact]
    public void IsPlaylistUrl_DetectsSoundCloudSet()
    {
        var svc = BuildService(out _, out _, out _);
        svc.IsPlaylistUrl("https://soundcloud.com/user/sets/mix").Should().BeTrue();
    }

    [Fact]
    public void IsPlaylistUrl_RejectsSingleTrack()
    {
        var svc = BuildService(out _, out _, out _);
        svc.IsPlaylistUrl("https://youtube.com/watch?v=dQw4w9WgXcQ").Should().BeFalse();
        svc.IsPlaylistUrl("Never Gonna Give You Up").Should().BeFalse();
    }

    [Fact]
    public void LooksLikeUrl_DetectsHttpUrls()
    {
        var svc = BuildService(out _, out _, out _);
        svc.LooksLikeUrl("https://example.com/track.mp3").Should().BeTrue();
        svc.LooksLikeUrl("http://example.com/track.mp3").Should().BeTrue();
        svc.LooksLikeUrl("just a search query").Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAndQueueAsync_Throws_WhenTrackNotFound()
    {
        var svc = BuildService(out var audio, out _, out _);
        audio.Tracks.LoadTrackAsync(Arg.Any<string>(), Arg.Any<TrackSearchMode>(), default, default)
            .Returns(new ValueTask<LavalinkTrack?>((LavalinkTrack?)null));

        var act = () => svc.ResolveAndQueueAsync("unknown track", "u1", "User", "1");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Couldn't resolve*");
    }

    [Fact]
    public async Task ResolveAndQueueAsync_MapsLavalinkTrackCorrectly()
    {
        var svc = BuildService(out var audio, out var queueRegistry, out var metrics);

        var lavalinkTrack = new LavalinkTrack
        {
            Title = "Test Song",
            Author = "Test Artist",
            Duration = TimeSpan.FromSeconds(180),
            Identifier = "abc123",
            SourceName = "youtube",
            Uri = new Uri("https://youtube.com/watch?v=abc123"),
        };

        audio.Tracks.LoadTrackAsync(Arg.Any<string>(), Arg.Any<TrackSearchMode>(), default, default)
            .Returns(new ValueTask<LavalinkTrack?>(lavalinkTrack));

        var result = await svc.ResolveAndQueueAsync("test query", "u1", "User", "1");

        result.Should().NotBeNull();
        result.Title.Should().Be("Test Song");
        result.Artist.Should().Be("Test Artist");
        result.SourceType.Should().Be("youtube");
        result.SourceId.Should().Be("abc123");
        result.DurationSeconds.Should().Be(180);
        result.RequestedByUserId.Should().Be("u1");
        result.RequestedByDisplayName.Should().Be("User");

        // Metrics should have been recorded
        await metrics.Received().IncrementBatchAsync("1",
            Arg.Is<System.Collections.Generic.IReadOnlyCollection<MetricIncrement>>(
                c => c.Count >= 2));
    }

    [Fact]
    public async Task ResolveAndQueueAsync_UsesSearchForNonUrl()
    {
        var svc = BuildService(out var audio, out _, out _);

        var lavalinkTrack = new LavalinkTrack
        {
            Title = "Found",
            Author = "Artist",
            Duration = TimeSpan.FromSeconds(60),
            Identifier = "found123",
            SourceName = "youtube",
        };

        audio.Tracks.LoadTrackAsync(
                Arg.Any<string>(),
                Arg.Is<TrackSearchMode>(m => m == TrackSearchMode.YouTube),
                default, default)
            .Returns(new ValueTask<LavalinkTrack?>(lavalinkTrack));

        var result = await svc.ResolveAndQueueAsync("search query", "u1", "User", "1");

        result.Should().NotBeNull();
        result.Title.Should().Be("Found");
    }

    [Fact]
    public async Task ResolveAndQueueAsync_UsesNoneForUrl()
    {
        var svc = BuildService(out var audio, out _, out _);

        var lavalinkTrack = new LavalinkTrack
        {
            Title = "From URL",
            Author = "Artist",
            Duration = TimeSpan.FromSeconds(60),
            Identifier = "url123",
            SourceName = "http",
            Uri = new Uri("https://example.com/track.mp3"),
        };

        audio.Tracks.LoadTrackAsync(
                Arg.Any<string>(),
                Arg.Is<TrackSearchMode>(m => m == TrackSearchMode.None),
                default, default)
            .Returns(new ValueTask<LavalinkTrack?>(lavalinkTrack));

        var result = await svc.ResolveAndQueueAsync("https://example.com/track.mp3", "u1", "User", "1");

        result.Should().NotBeNull();
        result.SourceType.Should().Be("mp3_upload");
    }
}
