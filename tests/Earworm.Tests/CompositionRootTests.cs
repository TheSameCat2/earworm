using System;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Earworm;
using Earworm.Config;
using Earworm.Discord;
using Earworm.Domain.DJ;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Persistence;
using Earworm.Persistence.Schema;

namespace Earworm.Tests;

/// <summary>
/// Smoke tests for the DI composition root. These exist to catch wiring bugs of the
/// shape "service X depends on Y but Y was never registered" — the kind that surface
/// at the first GetRequiredService<T> call against a real container, not in mocked
/// unit tests.
/// </summary>
public sealed class CompositionRootTests
{
    private static EarwormConfig BuildTestConfig() => new()
    {
        Discord = new DiscordConfig { GuildId = "1" },
        Dj = new DjConfig { Tts = new TtsConfig { VoiceId = "test" } },
        Persistence = new PersistenceConfig { SqlitePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"earworm-cr-{Guid.NewGuid():N}.db") },
        Cache = new CacheConfig { Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"earworm-cache-{Guid.NewGuid():N}") },
        Lavalink = new LavalinkConfig { Host = "localhost", Port = 2333, Password = "youshallnotpass" }
    };

    private static ServiceProvider BuildProvider()
    {
        Environment.SetEnvironmentVariable("EARWORM_DISCORD_BOT_TOKEN", "placeholder-token");
        Environment.SetEnvironmentVariable("EARWORM_GEMINI_API_KEY", "placeholder-key");
        Environment.SetEnvironmentVariable("EARWORM_ELEVENLABS_API_KEY", "placeholder-key");

        var services = new ServiceCollection();
        Program.ConfigureServices(services, BuildTestConfig(), "placeholder-token");
        var provider = services.BuildServiceProvider(validateScopes: true);

        // Match production startup order: migrate before any domain singletons
        // try to read from the database.
        var stateStore = provider.GetRequiredService<StateStore>();
        var migrator = new SchemaMigrator(stateStore.ConnectionString, NullLogger<SchemaMigrator>.Instance);
        migrator.Migrate();

        return provider;
    }

    [Fact]
    public async Task Non_Discord_Services_Should_Resolve()
    {
        // We use await-using because Lavalink4NET's IAudioService implements
        // IAsyncDisposable only (no sync Dispose). The ServiceProvider's sync
        // dispose throws when it tries to clean it up.
        await using var provider = BuildProvider();

        provider.GetRequiredService<StateStore>().Should().NotBeNull();
        provider.GetRequiredService<QueueManager>().Should().NotBeNull();
        provider.GetRequiredService<PlayerEngine>().Should().NotBeNull();
        // DJEngine pulls in GeminiClient and ITtsProvider, which transitively
        // pull in HttpClient. Regression guard against the original startup
        // crash (Microsoft.Extensions.Http not registered).
        provider.GetRequiredService<DJEngine>().Should().NotBeNull();
        // IAudioService should resolve too — this is what was missing pre-pivot.
        provider.GetRequiredService<Lavalink4NET.IAudioService>().Should().NotBeNull();
    }
}
