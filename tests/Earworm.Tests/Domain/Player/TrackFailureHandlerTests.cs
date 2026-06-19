using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using DSharpPlus;
using Earworm;
using Earworm.Config;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Infrastructure;
using Earworm.Persistence.Repositories;

namespace Earworm.Tests.Domain.Player;

public sealed class TrackFailureHandlerTests
{
    private static EarwormConfig BuildConfig() => new()
    {
        Discord = new DiscordConfig { GuildId = "1" },
    };

    /// <summary>
    /// DSharpPlus DiscordClient is sealed; we create a minimal real instance
    /// for tests that accept a DiscordClient parameter. It won't actually
    /// connect anywhere since we never call ConnectAsync.
    /// </summary>
    private static DiscordClient CreatePlaceholderClient() => new(new DiscordConfiguration
    {
        Token = "placeholder",
        TokenType = TokenType.Bot,
        Intents = DiscordIntents.AllUnprivileged,
        LoggerFactory = NullLoggerFactory.Instance,
        MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.None,
    });

    [Fact]
    public void OnTrackFailed_DoesNotThrow_WhenNoLoggingChannelConfigured()
    {
        var config = BuildConfig();
        using var discord = CreatePlaceholderClient();
        var settings = Substitute.For<ISettingsRepository>();
        settings.GetLoggingChannelIdAsync(Arg.Any<string>()).Returns((ulong?)null);

        var playerRegistry = new PerGuildRegistry<PlayerEngine>(_ => Substitute.For<PlayerEngine>(
            Substitute.For<Lavalink4NET.IAudioService>(),
            Substitute.For<QueueManager>(
                Substitute.For<IQueueRepository>(),
                Substitute.For<ISnapshotRepository>(),
                config,
                NullLogger<QueueManager>.Instance,
                "1"),
            Substitute.For<IQueueRepository>(),
            Substitute.For<IHistoryRepository>(),
            Substitute.For<IMetricsRepository>(),
            new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance),
            config,
            NullLogger<PlayerEngine>.Instance,
            new ShutdownLifetime(),
            "1"));

        var handler = new TrackFailureHandler(
            playerRegistry, discord, settings,
            NullLogger<TrackFailureHandler>.Instance, new ShutdownLifetime());

        var track = new QueueItem
        {
            Title = "Failing Track", Artist = "Nobody",
            SourceType = "youtube", SourceId = "badid",
            RequestedByUserId = "u1", RequestedByDisplayName = "User", GuildId = "1"
        };

        // Act — invoke via reflection. Should not throw even though no logging channel.
        var method = typeof(TrackFailureHandler).GetMethod("OnTrackFailed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var act = () => method!.Invoke(handler, new object[] { track, "Test failure" });
        act.Should().NotThrow();
    }

    [Fact]
    public void TrackFailureHandler_Dispose_DoesNotThrowWithNoEngines()
    {
        var config = BuildConfig();
        using var discord = CreatePlaceholderClient();
        var settings = Substitute.For<ISettingsRepository>();
        var playerRegistry = new PerGuildRegistry<PlayerEngine>(_ =>
            Substitute.For<PlayerEngine>(
                Substitute.For<Lavalink4NET.IAudioService>(),
                Substitute.For<QueueManager>(
                    Substitute.For<IQueueRepository>(),
                    Substitute.For<ISnapshotRepository>(),
                    config,
                    NullLogger<QueueManager>.Instance,
                    "1"),
                Substitute.For<IQueueRepository>(),
                Substitute.For<IHistoryRepository>(),
                Substitute.For<IMetricsRepository>(),
                new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance),
                config,
                NullLogger<PlayerEngine>.Instance,
                new ShutdownLifetime(),
                "1"));

        var handler = new TrackFailureHandler(
            playerRegistry, discord, settings,
            NullLogger<TrackFailureHandler>.Instance, new ShutdownLifetime());

        var act = () => handler.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task OnTrackFailed_FiresAndFailsSilently_WhenDiscordChannelDoesNotExist()
    {
        var config = BuildConfig();
        using var discord = CreatePlaceholderClient();
        var settings = Substitute.For<ISettingsRepository>();
        settings.GetLoggingChannelIdAsync(Arg.Any<string>()).Returns(999UL);

        var playerRegistry = new PerGuildRegistry<PlayerEngine>(_ => Substitute.For<PlayerEngine>(
            Substitute.For<Lavalink4NET.IAudioService>(),
            Substitute.For<QueueManager>(
                Substitute.For<IQueueRepository>(),
                Substitute.For<ISnapshotRepository>(),
                config,
                NullLogger<QueueManager>.Instance,
                "1"),
            Substitute.For<IQueueRepository>(),
            Substitute.For<IHistoryRepository>(),
            Substitute.For<IMetricsRepository>(),
            new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance),
            config,
            NullLogger<PlayerEngine>.Instance,
            new ShutdownLifetime(),
            "1"));

        var handler = new TrackFailureHandler(
            playerRegistry, discord, settings,
            NullLogger<TrackFailureHandler>.Instance, new ShutdownLifetime());

        var track = new QueueItem
        {
            Title = "Failing Track", Artist = "Nobody",
            SourceType = "youtube", SourceId = "badid",
            RequestedByUserId = "u1", RequestedByDisplayName = "User", GuildId = "1"
        };

        // Act — should not throw even though Discord call will fail (no real connection)
        var method = typeof(TrackFailureHandler).GetMethod("OnTrackFailed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var act = () => method!.Invoke(handler, new object[] { track, "Test failure" });
        act.Should().NotThrow();

        // Give the fire-and-forget task time to run
        await Task.Delay(200);
    }
}
