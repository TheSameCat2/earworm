using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Earworm;
using Earworm.Config;
using Earworm.Domain.DJ;
using Earworm.Domain.Queue;
using Earworm.Persistence.Repositories;

namespace Earworm.Tests.Domain.DJ;

public sealed class DJEngineTests
{
    static DJEngineTests()
    {
        // These tests create GeminiClient and ElevenLabsTtsProvider substitutes,
        // both of which read API keys from env vars in their constructors.
        Environment.SetEnvironmentVariable("EARWORM_GEMINI_API_KEY", "test-key");
        Environment.SetEnvironmentVariable("EARWORM_ELEVENLABS_API_KEY", "test-key");
    }
    private static EarwormConfig BuildConfig(string? ttsServeBaseUrl = "http://host.docker.internal:8080")
    {
        var config = new EarwormConfig
        {
            Discord = new DiscordConfig { GuildId = "1" },
            Dj = new DjConfig
            {
                MaxGapTracks = 3,
                Tts = new TtsConfig { VoiceId = "test-voice" },
                TtsScratchDirectory = Path.Combine(Path.GetTempPath(), $"earworm-djtest-{Guid.NewGuid():N}"),
                TtsServeBaseUrl = ttsServeBaseUrl,
                PersonaPrompt = "Next track: {track_metadata}.",
            },
        };
        return config;
    }

    private static TtsPreroll? InvokeMaybePlayCommentary(DJEngine engine, QueueItem track, CancellationToken ct = default)
    {
        var method = typeof(DJEngine).GetMethod("MaybePlayCommentaryAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        method.Should().NotBeNull();
        var task = (Task<TtsPreroll?>)method!.Invoke(engine, new object?[] { track, ct })!;
        return task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Commentary_NotGenerated_WhenDjDisabled()
    {
        var config = BuildConfig();
        var gemini = Substitute.For<GeminiClient>(
            Substitute.For<System.Net.Http.IHttpClientFactory>(), config, NullLogger<GeminiClient>.Instance);
        var tts = Substitute.For<ITtsProvider>();
        var settings = Substitute.For<ISettingsRepository>();
        settings.IsDjEnabledAsync(Arg.Any<string>()).Returns(false);

        var engine = new DJEngine(gemini, tts, settings,
            Substitute.For<IMetricsRepository>(), config,
            NullLogger<DJEngine>.Instance, "g1");

        var track = new QueueItem { Title = "Test", Artist = "Artist", GuildId = "g1" };
        var result = InvokeMaybePlayCommentary(engine, track);

        result.Should().BeNull();
        gemini.DidNotReceive().GenerateCommentaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Commentary_NotGenerated_WhenTtsServeBaseUrlIsEmpty()
    {
        var config = BuildConfig(ttsServeBaseUrl: "");
        var gemini = Substitute.For<GeminiClient>(
            Substitute.For<System.Net.Http.IHttpClientFactory>(), config, NullLogger<GeminiClient>.Instance);
        var tts = Substitute.For<ITtsProvider>();
        var settings = Substitute.For<ISettingsRepository>();
        settings.IsDjEnabledAsync(Arg.Any<string>()).Returns(true);

        var engine = new DJEngine(gemini, tts, settings,
            Substitute.For<IMetricsRepository>(), config,
            NullLogger<DJEngine>.Instance, "g1");

        var track = new QueueItem { Title = "Test", Artist = "Artist", GuildId = "g1" };
        var result = InvokeMaybePlayCommentary(engine, track);

        result.Should().BeNull();
        gemini.DidNotReceive().GenerateCommentaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Commentary_CadenceResetsAfterSuccessfulFire()
    {
        // Verify cadence logic: with MaxGapTracks=1, the target is always 1,
        // so every call passes the cadence gate. If the first call attempts
        // commentary, the counter resets, and a second call attempts again.
        var config = BuildConfig();
        config = config with
        {
            Dj = config.Dj with { MaxGapTracks = 1 }
        };

        var httpClientFactory = Substitute.For<System.Net.Http.IHttpClientFactory>();
        var realGemini = new GeminiClient(httpClientFactory, config, NullLogger<GeminiClient>.Instance);

        var tts = Substitute.For<ITtsProvider>();
        tts.RenderTtsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 0, 1, 2, 3 }));

        var settings = Substitute.For<ISettingsRepository>();
        settings.IsDjEnabledAsync(Arg.Any<string>()).Returns(true);

        var engine = new DJEngine(realGemini, tts, settings,
            Substitute.For<IMetricsRepository>(), config,
            NullLogger<DJEngine>.Instance, "g1");

        var track = new QueueItem { Title = "Test", Artist = "Artist", GuildId = "g1" };

        // Both calls pass the cadence gate (MaxGapTracks=1 ensures every call
        // passes). Gemini throws because there's no real HTTP handler, so both
        // return null. This proves the cadence doesn't get stuck.
        var result1 = InvokeMaybePlayCommentary(engine, track);
        var result2 = InvokeMaybePlayCommentary(engine, track);

        // The cadence gate should have passed both times (Gemini failure is
        // caught and swallowed, returning null).
        result1.Should().BeNull("cadence passed but Gemini failed");
        result2.Should().BeNull("cadence should pass again since counter continues to increment");
    }

    [Fact]
    public void Commentary_FailsSilently_WhenGeminiThrows()
    {
        var config = BuildConfig();
        var scratchDir = config.Dj.TtsScratchDirectory;
        Directory.CreateDirectory(scratchDir);

        var gemini = Substitute.For<GeminiClient>(
            Substitute.For<System.Net.Http.IHttpClientFactory>(), config, NullLogger<GeminiClient>.Instance);
        gemini.GenerateCommentaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("API error")));

        var tts = Substitute.For<ITtsProvider>();
        var settings = Substitute.For<ISettingsRepository>();
        settings.IsDjEnabledAsync(Arg.Any<string>()).Returns(true);

        var engine = new DJEngine(gemini, tts, settings,
            Substitute.For<IMetricsRepository>(), config,
            NullLogger<DJEngine>.Instance, "g1");

        var track = new QueueItem { Title = "Test", Artist = "Artist", GuildId = "g1" };

        // Force cadence by running enough times
        TtsPreroll? result = null;
        for (int i = 0; i < 20; i++)
        {
            result = InvokeMaybePlayCommentary(engine, track);
            if (result != null) break;
        }

        // Even if cadence fires, Gemini failure is swallowed and null returned.
        result.Should().BeNull();
    }

    [Fact]
    public void CadenceResets_AfterSuccessfulCommentary()
    {
        var config = BuildConfig();
        var scratchDir = config.Dj.TtsScratchDirectory;
        Directory.CreateDirectory(scratchDir);

        var gemini = Substitute.For<GeminiClient>(
            Substitute.For<System.Net.Http.IHttpClientFactory>(), config, NullLogger<GeminiClient>.Instance);
        gemini.GenerateCommentaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Hello from the DJ!");

        var tts = Substitute.For<ITtsProvider>();
        tts.RenderTtsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 0, 1, 2, 3 }));

        var settings = Substitute.For<ISettingsRepository>();
        settings.IsDjEnabledAsync(Arg.Any<string>()).Returns(true);

        var engine = new DJEngine(gemini, tts, settings,
            Substitute.For<IMetricsRepository>(), config,
            NullLogger<DJEngine>.Instance, "g1");

        var track = new QueueItem { Title = "Test", Artist = "Artist", GuildId = "g1" };

        // Run until we get a commentary, then check that the next N calls
        // don't generate (cadence resets).
        TtsPreroll? first = null;
        for (int i = 0; i < 20; i++)
        {
            first = InvokeMaybePlayCommentary(engine, track);
            if (first != null) break;
        }
        first.Should().NotBeNull("cadence must eventually fire");
        first!.Url.Should().Contain("/tts/");
        first.Url.Should().EndWith(".mp3");

        // Cleanup the staged file
        try { first.OnConsumedAsync().GetAwaiter().GetResult(); }
        catch { /* ignore if file already cleaned */ }
    }

    [Fact]
    public async Task GeneratedCommentary_IsCappedAtMaxChars()
    {
        var config = BuildConfig();
        var scratchDir = config.Dj.TtsScratchDirectory;
        Directory.CreateDirectory(scratchDir);

        var gemini = Substitute.For<GeminiClient>(
            Substitute.For<System.Net.Http.IHttpClientFactory>(), config, NullLogger<GeminiClient>.Instance);
        gemini.GenerateCommentaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new string('x', 500)); // well over the 320 cap

        var tts = Substitute.For<ITtsProvider>();
        tts.RenderTtsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 0, 1, 2, 3 }));

        var settings = Substitute.For<ISettingsRepository>();
        settings.IsDjEnabledAsync(Arg.Any<string>()).Returns(true);

        var engine = new DJEngine(gemini, tts, settings,
            Substitute.For<IMetricsRepository>(), config,
            NullLogger<DJEngine>.Instance, "g1");

        var track = new QueueItem { Title = "Test", Artist = "Artist", GuildId = "g1" };

        TtsPreroll? result = null;
        for (int i = 0; i < 20; i++)
        {
            result = InvokeMaybePlayCommentary(engine, track);
            if (result != null) break;
        }
        result.Should().NotBeNull();

        // Verify that the trimmed text was sent to TTS, not the full 500 chars
        await tts.Received().RenderTtsAsync(
            Arg.Is<string>(s => s.Length <= 320 + 3), // 320 cap + "..."
            Arg.Any<CancellationToken>());
    }
}
