using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using DSharpPlus;
using Lavalink4NET;
using Lavalink4NET.Players;
using Earworm.Config;
using Earworm;
using Earworm.Discord;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Infrastructure;
using Earworm.Persistence.Repositories;

namespace Earworm.Tests.Discord;

public sealed class VoiceManagerTests
{
    private static EarwormConfig BuildConfig() => new()
    {
        Discord = new DiscordConfig { GuildId = "1" },
        AutoBehavior = new AutoBehaviorConfig
        {
            // Long enough that the timer body never expires during the test,
            // so we observe only the GetOrAdd contention path.
            IdleDisconnectSeconds = 3600,
            EmptyChannelGraceSeconds = 3600
        }
    };

    private static DiscordClient BuildPlaceholderClient() => new(new DiscordConfiguration
    {
        Token = "placeholder",
        TokenType = TokenType.Bot,
        Intents = DiscordIntents.AllUnprivileged,
        LoggerFactory = NullLoggerFactory.Instance,
        MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.None
    });

    private static QueueManager BuildQueueManagerSub(EarwormConfig config) =>
        Substitute.For<QueueManager>(
            Substitute.For<IQueueRepository>(),
            Substitute.For<ISnapshotRepository>(),
            config,
            NullLogger<QueueManager>.Instance,
            "1");

    private static PlayerEngine BuildPlayerEngineSub(EarwormConfig config, QueueManager queueManager) =>
        Substitute.For<PlayerEngine>(
            Substitute.For<IAudioService>(),
            queueManager,
            Substitute.For<IQueueRepository>(),
            Substitute.For<IHistoryRepository>(),
            Substitute.For<IMetricsRepository>(),
            new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance),
            config,
            NullLogger<PlayerEngine>.Instance,
            new ShutdownLifetime(),
            "1");

    private static VoiceManager BuildVoiceManager(out object idleTimers) =>
        BuildVoiceManager(out idleTimers, out _);

    private static VoiceManager BuildVoiceManager(
        out object idleTimers,
        out PlayerEngine playerEngine) =>
        BuildVoiceManager(out idleTimers, out playerEngine, out _);

    private static VoiceManager BuildVoiceManager(
        out object idleTimers,
        out PlayerEngine playerEngine,
        out IPlayerManager playerManager)
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var engine = BuildPlayerEngineSub(config, queueManager);
        playerEngine = engine;
        var client = BuildPlaceholderClient();
        var audio = Substitute.For<IAudioService>();
        playerManager = Substitute.For<IPlayerManager>();
        audio.Players.Returns(playerManager);
        var playerRegistry = new PerGuildRegistry<PlayerEngine>(_ => engine);
        var queueRegistry = new PerGuildRegistry<QueueManager>(_ => queueManager);
        var vm = new VoiceManager(client, audio, playerRegistry, queueRegistry, config, NullLogger<VoiceManager>.Instance);
        // VoiceManager registers an initializer for existing and future engines;
        // publish this substitute through the registry so that wiring actually runs.
        playerRegistry.GetOrCreate("1");

        var field = typeof(VoiceManager).GetField("_idleTimers", BindingFlags.NonPublic | BindingFlags.Instance);
        idleTimers = field!.GetValue(vm)!;
        return vm;
    }

    private static int TimerCount(object timers) =>
        (int)timers.GetType().GetProperty("Count")!.GetValue(timers)!;

    private static bool ContainsTimer(object timers, ulong guildId) =>
        (bool)timers.GetType().GetMethod("ContainsKey")!.Invoke(timers, new object[] { guildId })!;

    [Fact]
    public void PlaybackBecameIdle_StartsTimer_WithoutAnotherTrackEndedEvent()
    {
        using var vm = BuildVoiceManager(out var idleTimers, out var playerEngine);

        playerEngine.PlaybackBecameIdle += Raise.Event<Action<string>>("1");

        ContainsTimer(idleTimers, 1UL).Should().BeTrue(
            "PlayerEngine may become idle after consuming failed queue entries without another TrackEnded event");
    }

    [Fact]
    public async Task StartIdleTimer_ConcurrentCalls_AddsExactlyOneTimer()
    {
        using var vm = BuildVoiceManager(out var idleTimers);
        var method = typeof(VoiceManager).GetMethod("StartIdleTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;

        const ulong guildId = 4242UL;
        const int parallelism = 64;
        using var startGate = new ManualResetEventSlim(false);

        var tasks = new Task[parallelism];
        for (int i = 0; i < parallelism; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                startGate.Wait();
                method.Invoke(vm, new object[] { guildId });
            });
        }

        startGate.Set();
        await Task.WhenAll(tasks);

        TimerCount(idleTimers).Should().Be(1, "GetOrAdd must ensure exactly one timer survives a contended race");
        ContainsTimer(idleTimers, guildId).Should().BeTrue();
    }

    [Fact]
    public async Task StartIdleTimer_ConcurrentCallsAcrossGuilds_OnePerGuild()
    {
        using var vm = BuildVoiceManager(out var idleTimers);
        var method = typeof(VoiceManager).GetMethod("StartIdleTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;

        ulong[] guilds = { 1UL, 2UL, 3UL, 4UL };
        const int callsPerGuild = 32;
        using var startGate = new ManualResetEventSlim(false);

        var tasks = new Task[guilds.Length * callsPerGuild];
        int idx = 0;
        foreach (var g in guilds)
        {
            for (int i = 0; i < callsPerGuild; i++)
            {
                tasks[idx++] = Task.Run(() =>
                {
                    startGate.Wait();
                    method.Invoke(vm, new object[] { g });
                });
            }
        }

        startGate.Set();
        await Task.WhenAll(tasks);

        TimerCount(idleTimers).Should().Be(guilds.Length);
        foreach (var g in guilds)
        {
            ContainsTimer(idleTimers, g).Should().BeTrue();
        }
    }

    [Fact]
    public async Task CancelGuildTimersAndDrainAsync_BlocksReplacementUntilReadmitted()
    {
        using var vm = BuildVoiceManager(out var idleTimers);
        var start = typeof(VoiceManager).GetMethod("StartIdleTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        const ulong guildId = 42UL;

        start.Invoke(vm, new object[] { guildId });
        ContainsTimer(idleTimers, guildId).Should().BeTrue();

        await vm.CancelGuildTimersAndDrainAsync(guildId).WaitAsync(TimeSpan.FromSeconds(2));
        TimerCount(idleTimers).Should().Be(0, "drain waits until the cancelled worker removes its own registration");

        start.Invoke(vm, new object[] { guildId });
        ContainsTimer(idleTimers, guildId).Should().BeFalse("suspended guilds cannot publish replacement timers");

        vm.AllowGuildTimers(guildId);
        start.Invoke(vm, new object[] { guildId });
        ContainsTimer(idleTimers, guildId).Should().BeTrue("explicit re-admission opens a fresh timer generation");
    }

    [Fact]
    public void JoinTimerBlock_IsReferenceCounted_AndDoesNotClearSuspension()
    {
        using var vm = BuildVoiceManager(out var idleTimers);
        var start = typeof(VoiceManager).GetMethod("StartIdleTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var begin = typeof(VoiceManager).GetMethod("BeginJoinTimerBlock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var end = typeof(VoiceManager).GetMethod("EndJoinTimerBlock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        const ulong guildId = 43UL;

        begin.Invoke(vm, new object[] { guildId });
        begin.Invoke(vm, new object[] { guildId });
        end.Invoke(vm, new object[] { guildId });
        start.Invoke(vm, new object[] { guildId });
        ContainsTimer(idleTimers, guildId).Should().BeFalse(
            "one overlapping join still owns the temporary timer block");

        vm.BlockGuildTimers(guildId);
        end.Invoke(vm, new object[] { guildId });
        start.Invoke(vm, new object[] { guildId });
        ContainsTimer(idleTimers, guildId).Should().BeFalse(
            "releasing the final join block must not clear a tenant suspension");

        vm.AllowGuildTimers(guildId);
        start.Invoke(vm, new object[] { guildId });
        ContainsTimer(idleTimers, guildId).Should().BeTrue();
    }

    [Fact]
    public async Task LeaveChannelAsync_WhenStopFails_StillAttemptsVoiceDisconnectLookup()
    {
        using var vm = BuildVoiceManager(out _, out var playerEngine, out var playerManager);
        playerManager
            .GetPlayerAsync<LavalinkPlayer>(1UL, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LavalinkPlayer?>((LavalinkPlayer?)null));

        // Force the concrete PlayerEngine.StopAsync path to fail before it can
        // query Lavalink. VoiceManager must still perform its independent
        // disconnect lookup and only then propagate the stop failure.
        var stopGateField = typeof(PlayerEngine).GetField("_stopGate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((SemaphoreSlim)stopGateField.GetValue(playerEngine)!).Dispose();

        Func<Task> act = () => vm.LeaveChannelAsync(1UL);
        await act.Should().ThrowAsync<ObjectDisposedException>();
        await playerManager.Received(1)
            .GetPlayerAsync<LavalinkPlayer>(1UL, Arg.Any<CancellationToken>());
    }
}
