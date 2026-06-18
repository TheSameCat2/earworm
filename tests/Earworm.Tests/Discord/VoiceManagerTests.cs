using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using DSharpPlus;
using Lavalink4NET;
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

    private static VoiceManager BuildVoiceManager(out ConcurrentDictionary<ulong, CancellationTokenSource> idleTimers)
    {
        var config = BuildConfig();
        var queueManager = BuildQueueManagerSub(config);
        var playerEngine = BuildPlayerEngineSub(config, queueManager);
        var client = BuildPlaceholderClient();
        var audio = Substitute.For<IAudioService>();
        var playerRegistry = new PerGuildRegistry<PlayerEngine>(_ => playerEngine);
        var queueRegistry = new PerGuildRegistry<QueueManager>(_ => queueManager);
        var vm = new VoiceManager(client, audio, playerRegistry, queueRegistry, config, NullLogger<VoiceManager>.Instance);

        var field = typeof(VoiceManager).GetField("_idleTimers", BindingFlags.NonPublic | BindingFlags.Instance);
        idleTimers = (ConcurrentDictionary<ulong, CancellationTokenSource>)field!.GetValue(vm)!;
        return vm;
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

        idleTimers.Count.Should().Be(1, "GetOrAdd must ensure exactly one CTS survives a contended race");
        idleTimers.ContainsKey(guildId).Should().BeTrue();
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

        idleTimers.Count.Should().Be(guilds.Length);
        foreach (var g in guilds)
        {
            idleTimers.ContainsKey(g).Should().BeTrue();
        }
    }
}
