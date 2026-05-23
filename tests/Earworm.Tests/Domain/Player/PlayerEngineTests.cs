using System;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Lavalink4NET;
using Lavalink4NET.Players;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Earworm.Config;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Persistence.Repositories;

namespace Earworm.Tests.Domain.Player;

public sealed class PlayerEngineTests
{
    private static EarwormConfig BuildConfig() => new()
    {
        Discord = new DiscordConfig { GuildId = "1" },
    };

    private static QueueManager BuildQueueManagerSub(EarwormConfig config)
    {
        // NSubstitute can substitute concrete classes; QueueManager's
        // DequeueAsync and the TrackQueued event are virtual.
        return Substitute.For<QueueManager>(
            Substitute.For<IQueueRepository>(),
            Substitute.For<ISnapshotRepository>(),
            config,
            NullLogger<QueueManager>.Instance);
    }

    private static PlayerEngine BuildEngine(
        IAudioService audioService,
        QueueManager queueManager,
        EarwormConfig config,
        out IQueueRepository queueRepo,
        out IMetricsRepository metricsRepo)
    {
        queueRepo = Substitute.For<IQueueRepository>();
        metricsRepo = Substitute.For<IMetricsRepository>();
        var historyRepo = Substitute.For<IHistoryRepository>();
        var transitions = new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance);
        return new PlayerEngine(
            audioService,
            queueManager,
            queueRepo,
            historyRepo,
            metricsRepo,
            transitions,
            config,
            NullLogger<PlayerEngine>.Instance);
    }

    private static Task InvokePlayNextAsync(PlayerEngine engine, LavalinkPlayer? player)
    {
        var method = typeof(PlayerEngine).GetMethod(
            "PlayNextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("PlayNextAsync must exist on PlayerEngine");
        return (Task)method!.Invoke(engine, new object?[] { player })!;
    }

    [Fact]
    public async Task PlayNextAsync_StopsAfterTenConsecutiveLoadFailures_AndDoesNotRecurse()
    {
        // Arrange
        var config = BuildConfig();

        // Queue manager that always returns a new failing item — simulating an
        // unbounded run of bad URLs.
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(_ => Task.FromResult<QueueItem?>(new QueueItem
        {
            SourceType = "youtube",
            SourceId = "bad",
            Title = "Bad",
            Artist = "Nobody",
            RequestedByUserId = "u",
            RequestedByDisplayName = "U",
            GuildId = "1",
        }));

        // Track manager that always returns null (Lavalink-style "not found").
        var trackManager = Substitute.For<ITrackManager>();
        trackManager
            .LoadTrackAsync(Arg.Any<string>(), Arg.Any<TrackSearchMode>(), default, default)
            .Returns(_ => new ValueTask<LavalinkTrack?>((LavalinkTrack?)null));

        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);

        var engine = BuildEngine(audioService, queueManager, config, out var queueRepo, out _);

        int failureEvents = 0;
        engine.TrackFailed += (_, _) => failureEvents++;

        // Act — should terminate without StackOverflowException and without
        // dequeueing forever.
        var task = InvokePlayNextAsync(engine, null);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(task, "the iterative loop must terminate at the failure cap, not run forever");
        await task; // surface any exception

        // Assert
        const int cap = 10;
        failureEvents.Should().Be(cap, "TrackFailed must fire once per failed load attempt up to the cap");
        await queueManager.Received(cap).DequeueAsync();

        // On cap, the engine transitions to idle and persists the idle state.
        engine.State.IsPlaying.Should().BeFalse();
        engine.State.IsPaused.Should().BeFalse();
        engine.State.CurrentSourceId.Should().BeNull();
        await queueRepo.Received().UpdatePlaybackStateAsync(Arg.Is<PlaybackState>(s => !s.IsPlaying && s.CurrentSourceId == null));
    }

    [Fact]
    public async Task PlayNextAsync_GoesIdleImmediately_WhenQueueIsEmpty()
    {
        // Arrange
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(Task.FromResult<QueueItem?>(null));

        var trackManager = Substitute.For<ITrackManager>();
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);

        var engine = BuildEngine(audioService, queueManager, config, out var queueRepo, out _);

        // Act
        await InvokePlayNextAsync(engine, null);

        // Assert
        await queueManager.Received(1).DequeueAsync();
        await trackManager.DidNotReceive().LoadTrackAsync(Arg.Any<string>(), Arg.Any<TrackSearchMode>(), default, default);
        await queueRepo.Received().UpdatePlaybackStateAsync(Arg.Is<PlaybackState>(s => !s.IsPlaying));
    }

    [Fact]
    public void State_Getter_ReturnsCachedInstance_WithoutAllocating()
    {
        // Arrange
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());

        var engine = BuildEngine(audioService, queueManager, config, out _, out _);

        // Act
        var a = engine.State;
        var b = engine.State;
        var c = engine.State;

        // Assert — reference equality proves no per-read allocation.
        ReferenceEquals(a, b).Should().BeTrue("State must be cached between reads");
        ReferenceEquals(b, c).Should().BeTrue("State must be cached between reads");
        a.IsPlaying.Should().BeFalse();
        a.IsPaused.Should().BeFalse();
        a.VoiceGuildId.Should().Be("1");
    }
}
