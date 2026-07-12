using System;
using System.Collections.Generic;
using System.Threading;
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
            audioService, queueRegistry, metrics, config,
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

    [Fact]
    public async Task ResolveAndQueueAsync_GuildWaiters_DoNotConsumeOtherGuildsGlobalCapacity()
    {
        var config = BuildConfig() with
        {
            Ops = new OpsConfig
            {
                MaxConcurrentTrackResolutions = 2,
                MaxConcurrentTrackResolutionsPerGuild = 2,
                MaxPendingTrackResolutionsPerGuild = 1,
            },
        };
        var trackManager = Substitute.For<ITrackManager>();
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);

        var queueRepository = Substitute.For<IQueueRepository>();
        queueRepository.GetQueueAsync(Arg.Any<string>()).Returns(new List<QueueItem>());
        long nextQueueItemId = 0;
        queueRepository.AddTrackAsync(Arg.Any<QueueItem>())
            .Returns(_ => Task.FromResult(Interlocked.Increment(ref nextQueueItemId)));
        var snapshotRepository = Substitute.For<ISnapshotRepository>();
        using var queueRegistry = new PerGuildRegistry<QueueManager>(guildId =>
            new QueueManager(
                queueRepository,
                snapshotRepository,
                config,
                NullLogger<QueueManager>.Instance,
                guildId));
        var metrics = Substitute.For<IMetricsRepository>();
        metrics.IncrementBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<MetricIncrement>>())
            .Returns(Task.CompletedTask);
        var service = new TrackQueuingService(
            audioService,
            queueRegistry,
            metrics,
            config,
            NullLogger<TrackQueuingService>.Instance);

        var firstGuildEnteredResolver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGuildEnteredResolver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResolvers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<LavalinkTrack?> ResolveAsync(string query)
        {
            if (query == "guild-a-1") firstGuildEnteredResolver.TrySetResult();
            if (query == "guild-b-1") secondGuildEnteredResolver.TrySetResult();
            await releaseResolvers.Task;
            return new LavalinkTrack
            {
                Title = query,
                Author = "Artist",
                Duration = TimeSpan.FromSeconds(60),
                Identifier = query,
                SourceName = "youtube",
            };
        }

        trackManager.LoadTrackAsync(Arg.Any<string>(), Arg.Any<TrackSearchMode>(), default, default)
            .Returns(call => new ValueTask<LavalinkTrack?>(ResolveAsync(call.ArgAt<string>(0))));

        var guildAFirst = service.ResolveAndQueueAsync("guild-a-1", "u1", "A", "1");
        await firstGuildEnteredResolver.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var guildAWaiter = service.ResolveAndQueueAsync("guild-a-2", "u2", "A", "1");
        Func<Task> overflow = async () => await service
            .ResolveAndQueueAsync("guild-a-3", "u4", "A", "1")
            .WaitAsync(TimeSpan.FromSeconds(1));
        await overflow.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pending*");
        var guildB = service.ResolveAndQueueAsync("guild-b-1", "u3", "B", "2");

        var observed = await Task.WhenAny(
            secondGuildEnteredResolver.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));
        releaseResolvers.TrySetResult();
        await Task.WhenAll(guildAFirst, guildAWaiter, guildB);

        observed.Should().BeSameAs(
            secondGuildEnteredResolver.Task,
            "requests waiting on guild A's local limit must not reserve every global slot");
    }

    [Theory]
    [InlineData("http://127.0.0.1/private.mp3")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.5/audio.mp3")]
    [InlineData("http://100.64.0.1/audio.mp3")]
    [InlineData("http://[::]/audio.mp3")]
    [InlineData("http://earworm/tts/file.mp3")]
    [InlineData("http://service.internal/audio.mp3")]
    [InlineData("http://user:password@example.com/audio.mp3")]
    public async Task ResolveAndQueueAsync_RejectsPrivateOrCredentialedUrls(string url)
    {
        var svc = BuildService(out var audio, out _, out _);

        var act = () => svc.ResolveAndQueueAsync(url, "u1", "User", "1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        await audio.Tracks.DidNotReceiveWithAnyArgs()
            .LoadTrackAsync(default!, default(TrackSearchMode), default, default);
    }
}
