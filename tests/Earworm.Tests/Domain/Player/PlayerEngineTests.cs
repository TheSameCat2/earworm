using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lavalink4NET;
using Lavalink4NET.Clients;
using Lavalink4NET.Events.Players;
using Lavalink4NET.Players;
using Lavalink4NET.Protocol.Models;
using Lavalink4NET.Protocol.Models.Filters;
using Lavalink4NET.Protocol.Payloads.Events;
using Lavalink4NET.Rest;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Earworm;
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
            NullLogger<QueueManager>.Instance,
            "1");
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
            NullLogger<PlayerEngine>.Instance,
            new ShutdownLifetime(),
            "1");
    }

    private static Task InvokePlayNextAsync(PlayerEngine engine, LavalinkPlayer? player)
    {
        var method = typeof(PlayerEngine).GetMethod(
            "PlayNextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("PlayNextAsync must exist on PlayerEngine");
        return (Task)method!.Invoke(engine, new object?[] { player })!;
    }

    private static Task InvokeTrackEndedAsync(
        PlayerEngine engine,
        LavalinkPlayer player,
        LavalinkTrack track,
        TrackEndReason reason)
    {
        var args = new TrackEndedEventArgs(player, track, reason);
        var classify = typeof(PlayerEngine).GetMethod(
            "ClassifyTrackEndReceiptLocked",
            BindingFlags.Instance | BindingFlags.NonPublic);
        classify.Should().NotBeNull("raw TrackEnded receipt classification must exist on PlayerEngine");
        var receipt = classify!.Invoke(engine, new object[] { args });

        var method = typeof(PlayerEngine).GetMethod(
            "HandleLavalinkTrackEndedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("the deterministic TrackEnded handler must exist on PlayerEngine");
        return (Task)method!.Invoke(engine, new[] { args, receipt, CancellationToken.None })!;
    }

    private static async Task InvokeTrackStartedReceiptAsync(
        PlayerEngine engine,
        LavalinkPlayer player,
        LavalinkTrack track)
    {
        var method = typeof(PlayerEngine).GetMethod(
            "OnLavalinkTrackStartedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("the synchronous TrackStarted fence must exist on PlayerEngine");
        var args = new TrackStartedEventArgs(player, track);
        await (Task)method!.Invoke(engine, new object[] { engine, args })!;
    }

    private static IPlayerProperties<LavalinkPlayer, LavalinkPlayerOptions> BuildPlayerProperties()
    {
        var properties = Substitute.For<IPlayerProperties<LavalinkPlayer, LavalinkPlayerOptions>>();
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        properties.ApiClient.Returns(Substitute.For<ILavalinkApiClient>());
        properties.DiscordClient.Returns(Substitute.For<IDiscordClientWrapper>());
        properties.InitialState.Returns(new PlayerInformationModel(
            GuildId: 1,
            CurrentTrack: null,
            Volume: 1,
            IsPaused: false,
            VoiceState: new VoiceStateModel("token", "endpoint", "voice-session"),
            Filters: new PlayerFilterMapModel()));
        properties.Label.Returns("test-player");
        properties.Logger.Returns(NullLogger<LavalinkPlayer>.Instance);
        properties.SystemClock.Returns(clock);
        properties.Options.Returns(Options.Create(new LavalinkPlayerOptions()));
        properties.VoiceChannelId.Returns(2UL);
        properties.SessionId.Returns("lavalink-session");
        properties.Lifecycle.Returns(Substitute.For<IPlayerLifecycle>());
        return properties;
    }

    private sealed class NonAcknowledgingLavalinkPlayer()
        : LavalinkPlayer(BuildPlayerProperties())
    {
        public int PlayCount { get; private set; }
        public int StopCount { get; private set; }
        public bool ThrowOnPlay { get; set; }
        public TaskCompletionSource PlayAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask PlayAsync(
            ITrackQueueItem trackQueueItem,
            TrackPlayProperties properties = default,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlayCount++;
            PlayAttempted.TrySetResult();
            if (ThrowOnPlay) throw new IOException("ambiguous play transport failure");
            return ValueTask.CompletedTask;
        }

        public override ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            // Deliberately do not raise TrackEnded; the engine's stop wait must
            // time out so the regression can deliver the old event later.
            return ValueTask.CompletedTask;
        }

        public override ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private static (PlayerEngine Engine, QueueManager Queue, IQueueRepository QueueRepository) BuildRestoreEngine(
        ISnapshotRepository snapshotRepository,
        QueueItem currentTrack)
    {
        var config = BuildConfig();
        var queueRepository = Substitute.For<IQueueRepository>();
        queueRepository.GetQueueAsync("1").Returns(new List<QueueItem>());
        queueRepository.AddTrackAtFrontAsync(Arg.Any<QueueItem>()).Returns(42L);
        var queueManager = new QueueManager(
            queueRepository,
            snapshotRepository,
            config,
            NullLogger<QueueManager>.Instance,
            "1");
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        var engine = new PlayerEngine(
            audioService,
            queueManager,
            queueRepository,
            Substitute.For<IHistoryRepository>(),
            Substitute.For<IMetricsRepository>(),
            new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance),
            config,
            NullLogger<PlayerEngine>.Instance,
            new ShutdownLifetime(),
            "1");

        typeof(PlayerEngine)
            .GetField("_currentTrack", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, currentTrack);
        typeof(PlayerEngine)
            .GetMethod("RebuildStateLocked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, null);

        return (engine, queueManager, queueRepository);
    }

    private static bool IsAdvanceGenerationCanceled(PlayerEngine engine)
    {
        var cts = (CancellationTokenSource)typeof(PlayerEngine)
            .GetField("_advanceCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(engine)!;
        return cts.IsCancellationRequested;
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
        int idleEvents = 0;
        engine.TrackFailed += (_, _) => throw new InvalidOperationException("subscriber failed");
        engine.TrackFailed += (_, _) => failureEvents++;
        engine.PlaybackBecameIdle += _ => idleEvents++;

        // Act — should terminate without StackOverflowException and without
        // dequeueing forever.
        var task = InvokePlayNextAsync(engine, null);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(task, "the iterative loop must terminate at the failure cap, not run forever");
        await task; // surface any exception

        // Assert
        const int cap = 10;
        failureEvents.Should().Be(cap, "TrackFailed must fire once per failed load attempt up to the cap");
        idleEvents.Should().Be(1, "exhausting the load-failure cap must signal that playback became idle");
        await queueManager.Received(cap).DequeueAsync();

        // On cap, the engine transitions to idle and persists the idle state.
        engine.State.IsPlaying.Should().BeFalse();
        engine.State.IsPaused.Should().BeFalse();
        engine.State.CurrentSourceId.Should().BeNull();
        await queueRepo.Received().UpdatePlaybackStateAsync(Arg.Any<string>(), Arg.Is<PlaybackState>(s => !s.IsPlaying && s.CurrentSourceId == null));
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
        int idleEvents = 0;
        engine.PlaybackBecameIdle += _ => idleEvents++;

        // Act
        await InvokePlayNextAsync(engine, null);

        // Assert
        await queueManager.Received(1).DequeueAsync();
        idleEvents.Should().Be(1, "an empty queue must signal that playback became idle");
        await trackManager.DidNotReceive().LoadTrackAsync(Arg.Any<string>(), Arg.Any<TrackSearchMode>(), default, default);
        await queueRepo.Received().UpdatePlaybackStateAsync(Arg.Any<string>(), Arg.Is<PlaybackState>(s => !s.IsPlaying));
    }

    [Fact]
    public async Task PlayNextAsync_ConcurrentAdvancement_IsSerializedPerGuild()
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        int activeDequeues = 0;
        int maxActiveDequeues = 0;
        queueManager.DequeueAsync().Returns(async _ =>
        {
            int active = Interlocked.Increment(ref activeDequeues);
            int observed;
            do
            {
                observed = Volatile.Read(ref maxActiveDequeues);
                if (active <= observed) break;
            }
            while (Interlocked.CompareExchange(ref maxActiveDequeues, active, observed) != observed);

            await Task.Delay(75);
            Interlocked.Decrement(ref activeDequeues);
            return (QueueItem?)null;
        });

        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);

        await Task.WhenAll(
            InvokePlayNextAsync(engine, null),
            InvokePlayNextAsync(engine, null));

        maxActiveDequeues.Should().Be(1, "queue advancement for one guild must never overlap");
        await queueManager.Received(2).DequeueAsync();
    }

    [Fact]
    public async Task StopAsync_CancelsInFlightPreroll_AndReturnsDequeuedTrackToFront()
    {
        var config = BuildConfig();
        var item = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "pending",
            Title = "Pending",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(item);
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        using var engine = BuildEngine(audioService, queueManager, config, out var queueRepo, out _);
        int idleEvents = 0;
        engine.PlaybackBecameIdle += _ => idleEvents++;
        var enteredHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SetPreTrackHook(async (_, cancellationToken) =>
        {
            enteredHook.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });

        var advance = InvokePlayNextAsync(engine, null);
        await enteredHook.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Func<Task> observeAdvance = async () => await advance;
        await observeAdvance.Should().ThrowAsync<OperationCanceledException>();

        await queueManager.Received(1).RequeueFrontAsync(item, notify: false);
        idleEvents.Should().Be(0, "an explicit stop must not masquerade as natural playback exhaustion");
        await queueRepo.Received().UpdatePlaybackStateAsync(
            "1", Arg.Is<PlaybackState>(state => !state.IsPlaying && state.CurrentSourceId == null));
    }

    [Fact]
    public async Task DelayedStoppedEvent_AfterStopTimeout_DoesNotEndSameIdentifierReplay()
    {
        var config = BuildConfig();
        var first = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "same-source",
            Title = "First play",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var replay = first with { Title = "Replay" };
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(
            Task.FromResult<QueueItem?>(first),
            Task.FromResult<QueueItem?>(replay),
            Task.FromResult<QueueItem?>(null));

        var track = new LavalinkTrack
        {
            Identifier = "same-lavalink-id",
            Title = "Same media",
            Author = "Artist"
        };
        var trackManager = Substitute.For<ITrackManager>();
        trackManager
            .LoadTrackAsync(
                Arg.Any<string>(),
                Arg.Any<TrackSearchMode>(),
                Arg.Any<LavalinkApiResolutionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkTrack?>(track));
        var player = new NonAcknowledgingLavalinkPlayer();
        var playerManager = Substitute.For<IPlayerManager>();
        playerManager
            .GetPlayerAsync<LavalinkPlayer>(1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkPlayer?>(player));
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);
        audioService.Players.Returns(playerManager);
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);
        int endedEvents = 0;
        engine.TrackEnded += (_, _, _) => throw new InvalidOperationException("subscriber failed");
        engine.TrackEnded += (_, _, _) => endedEvents++;

        await InvokePlayNextAsync(engine, player);
        await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        await engine.MaybeStartAsync();

        player.PlayCount.Should().Be(2);
        engine.State.IsPlaying.Should().BeTrue();
        engine.State.CurrentTitle.Should().Be("Replay");

        // This is the acknowledgement for the first StopAsync. Its Lavalink
        // identifier is intentionally identical to the live replay.
        await InvokeTrackEndedAsync(engine, player, track, TrackEndReason.Stopped);

        engine.State.IsPlaying.Should().BeTrue("the delayed stop belongs to the retired playback generation");
        engine.State.CurrentTitle.Should().Be("Replay");
        endedEvents.Should().Be(0);
        await queueManager.Received(2).DequeueAsync();

        // Normal completion for the replay must still end it and advance.
        await InvokeTrackEndedAsync(engine, player, track, TrackEndReason.Finished);
        engine.State.IsPlaying.Should().BeFalse();
        endedEvents.Should().Be(1);
        await queueManager.Received(3).DequeueAsync();
    }

    [Fact]
    public async Task PlayFailure_BestEffortStopsAmbiguousServerAcceptanceBeforeRequeue()
    {
        var config = BuildConfig();
        var item = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "ambiguous",
            Title = "Ambiguous",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(Task.FromResult<QueueItem?>(item));
        var track = new LavalinkTrack
        {
            Identifier = "ambiguous-id",
            Title = "Ambiguous",
            Author = "Artist"
        };
        var trackManager = Substitute.For<ITrackManager>();
        trackManager
            .LoadTrackAsync(
                Arg.Any<string>(),
                Arg.Any<TrackSearchMode>(),
                Arg.Any<LavalinkApiResolutionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkTrack?>(track));
        var player = new NonAcknowledgingLavalinkPlayer { ThrowOnPlay = true };
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);

        await InvokePlayNextAsync(engine, player);

        player.PlayCount.Should().Be(1);
        player.StopCount.Should().Be(1,
            "a transport failure may happen after Lavalink accepted the play request");
        await queueManager.Received(1).RequeueFrontAsync(item, notify: false);
        engine.State.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public async Task AmbiguousPlay_WithObservedStart_RetainsStopReceiptAcrossSameIdentifierRetry()
    {
        var config = BuildConfig();
        var item = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "ambiguous",
            Title = "Ambiguous",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(
            Task.FromResult<QueueItem?>(item),
            Task.FromResult<QueueItem?>(item));
        var track = new LavalinkTrack
        {
            Identifier = "ambiguous-id",
            Title = "Ambiguous",
            Author = "Artist"
        };
        var trackManager = Substitute.For<ITrackManager>();
        trackManager
            .LoadTrackAsync(
                Arg.Any<string>(),
                Arg.Any<TrackSearchMode>(),
                Arg.Any<LavalinkApiResolutionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkTrack?>(track));
        var player = new NonAcknowledgingLavalinkPlayer { ThrowOnPlay = true };
        var playerManager = Substitute.For<IPlayerManager>();
        playerManager
            .GetPlayerAsync<LavalinkPlayer>(1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkPlayer?>(player));
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);
        audioService.Players.Returns(playerManager);
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);

        var failedAdvance = InvokePlayNextAsync(engine, player);
        await player.PlayAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await InvokeTrackStartedReceiptAsync(engine, player, track);
        await failedAdvance.WaitAsync(TimeSpan.FromSeconds(4));

        player.ThrowOnPlay = false;
        await engine.MaybeStartAsync();
        engine.State.IsPlaying.Should().BeTrue();

        // The first generation was known to have started, so its unacknowledged
        // stop remains correlated even after the ambiguous-play wait expires.
        await InvokeTrackEndedAsync(engine, player, track, TrackEndReason.Stopped);
        engine.State.IsPlaying.Should().BeTrue();
        engine.State.CurrentSourceId.Should().Be("ambiguous");
    }

    [Fact]
    public async Task PlaybackStateWriteFailure_DoesNotHideSuccessfullyStartedAudio()
    {
        var config = BuildConfig();
        var item = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "live",
            Title = "Live",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(Task.FromResult<QueueItem?>(item));
        var track = new LavalinkTrack
        {
            Identifier = "live-id",
            Title = "Live",
            Author = "Artist"
        };
        var trackManager = Substitute.For<ITrackManager>();
        trackManager
            .LoadTrackAsync(
                Arg.Any<string>(),
                Arg.Any<TrackSearchMode>(),
                Arg.Any<LavalinkApiResolutionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkTrack?>(track));
        var player = new NonAcknowledgingLavalinkPlayer();
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);
        using var engine = BuildEngine(audioService, queueManager, config, out var queueRepository, out _);
        queueRepository
            .UpdatePlaybackStateAsync("1", Arg.Any<PlaybackState>())
            .Returns(Task.FromException(new IOException("sqlite write failed")));
        int startedEvents = 0;
        engine.TrackStarted += _ => startedEvents++;

        await InvokePlayNextAsync(engine, player);

        player.PlayCount.Should().Be(1);
        startedEvents.Should().Be(1);
        engine.State.IsPlaying.Should().BeTrue();
        await queueManager.DidNotReceive().RequeueFrontAsync(Arg.Any<QueueItem>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task TrackStartedFifo_CorrelatesDelayedOldStartAndStopBeforeSameIdentifierSkip()
    {
        var config = BuildConfig();
        var item = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "same-source",
            Title = "Same media",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(
            Task.FromResult<QueueItem?>(item),
            Task.FromResult<QueueItem?>(item),
            Task.FromResult<QueueItem?>(null));
        var track = new LavalinkTrack
        {
            Identifier = "same-lavalink-id",
            Title = "Same media",
            Author = "Artist"
        };
        var trackManager = Substitute.For<ITrackManager>();
        trackManager
            .LoadTrackAsync(
                Arg.Any<string>(),
                Arg.Any<TrackSearchMode>(),
                Arg.Any<LavalinkApiResolutionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkTrack?>(track));
        var player = new NonAcknowledgingLavalinkPlayer();
        var playerManager = Substitute.For<IPlayerManager>();
        playerManager
            .GetPlayerAsync<LavalinkPlayer>(1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkPlayer?>(player));
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(trackManager);
        audioService.Players.Returns(playerManager);
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);
        int endedEvents = 0;
        engine.TrackEnded += (_, _, _) => endedEvents++;

        await InvokePlayNextAsync(engine, player);
        await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        await engine.MaybeStartAsync();

        // Both TrackStarted receipts use the same identifier. FIFO receipt
        // correlation must attribute the first to generation 1, then its
        // delayed Stopped acknowledgement to generation 1, and only the second
        // TrackStarted to the live replay.
        await InvokeTrackStartedReceiptAsync(engine, player, track);
        await InvokeTrackEndedAsync(engine, player, track, TrackEndReason.Stopped);
        engine.State.IsPlaying.Should().BeTrue();

        await InvokeTrackStartedReceiptAsync(engine, player, track);
        await engine.SkipAsync();
        await InvokeTrackEndedAsync(engine, player, track, TrackEndReason.Stopped);

        engine.State.IsPlaying.Should().BeFalse();
        endedEvents.Should().Be(1);
        await queueManager.Received(3).DequeueAsync();
    }

    [Fact]
    public async Task DelayedTtsHandler_SignalsOnlyCompletionCapturedAtReceipt()
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var player = new NonAcknowledgingLavalinkPlayer();
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ttsTrack = new LavalinkTrack
        {
            Identifier = "tts-one",
            Title = "TTS one",
            Author = "Earworm"
        };
        typeof(PlayerEngine).GetField("_currentTtsIdentifier", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, "tts-one");
        typeof(PlayerEngine).GetField("_ttsCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, firstCompletion);
        var eventArgs = new TrackEndedEventArgs(player, ttsTrack, TrackEndReason.Finished);
        var classify = typeof(PlayerEngine).GetMethod(
            "ClassifyTrackEndReceiptLocked",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var receipt = classify.Invoke(engine, new object[] { eventArgs });

        // A newer generation installs its own TTS state before the offloaded
        // handler for the first receipt gets CPU time.
        typeof(PlayerEngine).GetField("_currentTtsIdentifier", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, "tts-two");
        typeof(PlayerEngine).GetField("_ttsCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, secondCompletion);
        var handle = typeof(PlayerEngine).GetMethod(
            "HandleLavalinkTrackEndedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)handle.Invoke(engine, new[] { eventArgs, receipt, CancellationToken.None })!;

        firstCompletion.Task.IsCompletedSuccessfully.Should().BeTrue();
        secondCompletion.Task.IsCompleted.Should().BeFalse();
        typeof(PlayerEngine).GetField("_currentTtsIdentifier", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(engine).Should().Be("tts-two");
    }

    [Fact]
    public async Task RetireAsync_PermanentlyPreventsMaybeStartFromRenewingPlayback()
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        queueManager.DequeueAsync().Returns(Task.FromResult<QueueItem?>(new QueueItem
        {
            SourceType = "youtube",
            SourceId = "must-not-start",
            Title = "Must not start",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        }));
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);

        await engine.RetireAsync();
        await engine.MaybeStartAsync();

        IsAdvanceGenerationCanceled(engine).Should().BeTrue();
        await queueManager.DidNotReceive().DequeueAsync();
    }

    [Fact]
    public async Task RetireAsync_WhenPlayerLookupFails_StillClearsAndPersistsIdleState()
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var playerManager = Substitute.For<IPlayerManager>();
        playerManager
            .GetPlayerAsync<LavalinkPlayer>(1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkPlayer?>(
                Task.FromException<LavalinkPlayer?>(new IOException("lavalink unavailable"))));
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        audioService.Players.Returns(playerManager);
        using var engine = BuildEngine(audioService, queueManager, config, out var queueRepository, out _);
        typeof(PlayerEngine)
            .GetField("_currentTrack", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, new QueueItem
            {
                SourceType = "youtube",
                SourceId = "stale",
                Title = "Stale",
                RequestedByUserId = "u",
                RequestedByDisplayName = "User",
                GuildId = "1"
            });
        typeof(PlayerEngine)
            .GetField("_currentMusicIdentifier", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, "stale-id");
        typeof(PlayerEngine)
            .GetMethod("RebuildStateLocked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, null);

        await engine.RetireAsync();

        engine.State.IsPlaying.Should().BeFalse();
        engine.State.CurrentSourceId.Should().BeNull();
        await queueRepository.Received().UpdatePlaybackStateAsync(
            "1", Arg.Is<PlaybackState>(state => !state.IsPlaying && state.CurrentSourceId == null));
    }

    [Fact]
    public async Task StopAsync_WhenIdlePersistenceFails_StillCompletesWithLocalIdleState()
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        using var engine = BuildEngine(audioService, queueManager, config, out var queueRepository, out _);
        typeof(PlayerEngine)
            .GetField("_currentTrack", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, new QueueItem
            {
                SourceType = "youtube",
                SourceId = "current",
                Title = "Current",
                RequestedByUserId = "u",
                RequestedByDisplayName = "User",
                GuildId = "1"
            });
        typeof(PlayerEngine)
            .GetMethod("RebuildStateLocked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, null);
        queueRepository
            .UpdatePlaybackStateAsync("1", Arg.Any<PlaybackState>())
            .Returns(Task.FromException(new IOException("sqlite unavailable")));

        await engine.StopAsync();

        engine.State.IsPlaying.Should().BeFalse();
        engine.State.CurrentSourceId.Should().BeNull();
    }

    [Fact]
    public async Task RetiredEngine_PublicControlsCannotReachReplacementGuildPlayer()
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var player = new NonAcknowledgingLavalinkPlayer();
        var playerManager = Substitute.For<IPlayerManager>();
        playerManager
            .GetPlayerAsync<LavalinkPlayer>(1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkPlayer?>(player));
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        audioService.Players.Returns(playerManager);
        using var engine = BuildEngine(audioService, queueManager, config, out _, out _);

        await engine.RetireAsync();

        Func<Task> pause = () => engine.PauseAsync();
        Func<Task> resume = () => engine.ResumeAsync();
        Func<Task> seek = () => engine.SeekAsync(TimeSpan.Zero);
        Func<Task> skip = () => engine.SkipAsync();
        await pause.Should().ThrowAsync<InvalidOperationException>();
        await resume.Should().ThrowAsync<InvalidOperationException>();
        await seek.Should().ThrowAsync<InvalidOperationException>();
        await skip.Should().ThrowAsync<InvalidOperationException>();
        await engine.StopAsync();
        await engine.MaybeStartAsync();

        await playerManager.Received(1)
            .GetPlayerAsync<LavalinkPlayer>(1, Arg.Any<CancellationToken>());
        player.StopCount.Should().Be(1,
            "only the first RetireAsync teardown may touch the guild player");
    }

    [Fact]
    public async Task RestoreSnapshotAsync_WhenSnapshotDisappears_RequeuesCurrentAndRenewsPlaybackGeneration()
    {
        var current = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "old-current",
            Title = "Old Current",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var snapshots = Substitute.For<ISnapshotRepository>();
        snapshots.RestoreSnapshotAsync("1")
            .Returns(Task.FromResult<(PlaybackState PlaybackState, List<QueueItem> QueueItems)?>(null));
        var (engine, queue, queueRepository) = BuildRestoreEngine(snapshots, current);
        using (engine)
        using (queue)
        {
            var restored = await engine.RestoreSnapshotAsync();

            restored.Should().BeNull();
            queue.GetQueue().Should().ContainSingle()
                .Which.SourceId.Should().Be("old-current");
            IsAdvanceGenerationCanceled(engine).Should().BeFalse(
                "the rollback path must renew the generation so playback can resume");
            await queueRepository.Received(1).AddTrackAtFrontAsync(
                Arg.Is<QueueItem>(item => item.SourceId == "old-current"));
        }
    }

    [Fact]
    public async Task RestoreSnapshotAsync_BlocksMaybeStartUntilRepositoryRestoreFinishes()
    {
        var current = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "old-current",
            Title = "Old Current",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var restoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource<
            (PlaybackState PlaybackState, List<QueueItem> QueueItems)?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = Substitute.For<ISnapshotRepository>();
        snapshots.RestoreSnapshotAsync("1").Returns(_ =>
        {
            restoreEntered.TrySetResult();
            return releaseRestore.Task;
        });
        var (engine, queue, _) = BuildRestoreEngine(snapshots, current);
        using (engine)
        using (queue)
        {
            var restore = engine.RestoreSnapshotAsync();
            await restoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await engine.MaybeStartAsync();
            IsAdvanceGenerationCanceled(engine).Should().BeTrue(
                "a join racing repository restore must not start the temporary rollback queue");
            queue.GetQueue().Should().ContainSingle()
                .Which.SourceId.Should().Be("old-current");

            releaseRestore.TrySetResult(null);
            (await restore).Should().BeNull();
            IsAdvanceGenerationCanceled(engine).Should().BeFalse(
                "the rollback path may restart only after the composite restore fence is released");
        }
    }

    [Fact]
    public async Task RestoreSnapshotAsync_WhenRestoreThrows_KeepsRollbackQueueAndRenewsPlaybackGeneration()
    {
        var current = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "old-current",
            Title = "Old Current",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var snapshots = Substitute.For<ISnapshotRepository>();
        snapshots.RestoreSnapshotAsync("1").Returns(Task.FromException<
            (PlaybackState PlaybackState, List<QueueItem> QueueItems)?>(new IOException("restore failed")));
        var (engine, queue, _) = BuildRestoreEngine(snapshots, current);
        using (engine)
        using (queue)
        {
            Func<Task> restore = () => engine.RestoreSnapshotAsync();
            await restore.Should().ThrowAsync<IOException>().WithMessage("restore failed");

            queue.GetQueue().Should().ContainSingle()
                .Which.SourceId.Should().Be("old-current");
            IsAdvanceGenerationCanceled(engine).Should().BeFalse();
        }
    }

    [Fact]
    public async Task RestoreSnapshotAsync_WhenSuccessful_ReplacesTemporaryRollbackItem()
    {
        var current = new QueueItem
        {
            SourceType = "youtube",
            SourceId = "old-current",
            Title = "Old Current",
            RequestedByUserId = "u",
            RequestedByDisplayName = "User",
            GuildId = "1"
        };
        var snapshotTrack = current with { SourceId = "snapshot-track", Title = "Snapshot Track" };
        var restoredPlayback = new PlaybackState { VoiceGuildId = "1" };
        var snapshots = Substitute.For<ISnapshotRepository>();
        snapshots.RestoreSnapshotAsync("1").Returns(Task.FromResult<
            (PlaybackState PlaybackState, List<QueueItem> QueueItems)?>(
                (restoredPlayback, new List<QueueItem> { snapshotTrack })));
        var (engine, queue, _) = BuildRestoreEngine(snapshots, current);
        using (engine)
        using (queue)
        {
            var restored = await engine.RestoreSnapshotAsync();

            restored.Should().BeSameAs(restoredPlayback);
            queue.GetQueue().Should().ContainSingle()
                .Which.SourceId.Should().Be("snapshot-track");
            IsAdvanceGenerationCanceled(engine).Should().BeTrue(
                "successful restore stays stopped until the command verifies or joins a voice channel");
        }
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

    [Fact]
    public void Dispose_CancelsOnlyThisEnginesLifetime_AndIsIdempotent()
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var audioService = Substitute.For<IAudioService>();
        audioService.Tracks.Returns(Substitute.For<ITrackManager>());
        var shutdown = new ShutdownLifetime();
        var engine = new PlayerEngine(
            audioService,
            queueManager,
            Substitute.For<IQueueRepository>(),
            Substitute.For<IHistoryRepository>(),
            Substitute.For<IMetricsRepository>(),
            new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance),
            config,
            NullLogger<PlayerEngine>.Instance,
            shutdown,
            "1");
        var tokenField = typeof(PlayerEngine).GetField("_lifetimeToken", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var token = (CancellationToken)tokenField.GetValue(engine)!;

        engine.Dispose();
        engine.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
        shutdown.IsShuttingDown.Should().BeFalse("evicting one guild must not cancel the process-wide lifetime");
        shutdown.Dispose();
    }
}
