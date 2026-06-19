using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Earworm.Config;
using Earworm.Domain.DJ;

namespace Earworm.Tests.Domain.DJ;

public sealed class GeminiClientTests
{
    private static EarwormConfig BuildConfig()
    {
        var config = new EarwormConfig
        {
            Discord = new DiscordConfig { GuildId = "1" },
            Dj = new DjConfig
            {
                Tts = new TtsConfig { VoiceId = "test" },
                PersonaPrompt = "Say something about {track_metadata}.",
            },
        };
        return config;
    }

    /// <summary>
    /// A stub handler that returns a fixed JSON body for any Gemini POST.
    /// Lets us exercise the parsing paths without a real network call.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    private static GeminiClient BuildClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(GeminiClient)).Returns(client);

        Environment.SetEnvironmentVariable("EARWORM_GEMINI_API_KEY", "test-key");
        return new GeminiClient(factory, BuildConfig(), NullLogger<GeminiClient>.Instance);
    }

    [Fact]
    public async Task GenerateCommentaryAsync_ReturnsText_WhenCandidatesPresent()
    {
        var json = @"{""candidates"":[{""content"":{""parts"":[{""text"":""  Up next, a classic.  ""}],""role"":""model""},""finishReason"":""STOP""}]}";
        var client = BuildClient(new StubHandler(json));

        var commentary = await client.GenerateCommentaryAsync("Title", "Artist", CancellationToken.None);

        commentary.Should().Be("Up next, a classic.");
    }

    [Fact]
    public async Task GenerateCommentaryAsync_ThrowsCleanMessage_WhenNoCandidatesBlockedBySafety()
    {
        // Regression: previously this crashed with an opaque index-out-of-range
        // on `candidates[0]`. Now it should throw a clear InvalidOperationException
        // surfaced from the promptFeedback blockReason, which the DJ caller catches.
        var json = @"{""promptFeedback"":{""blockReason"":""SAFETY""}}";
        var client = BuildClient(new StubHandler(json));

        var act = () => client.GenerateCommentaryAsync("Title", "Artist", CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*blockReason*SAFETY*");
    }

    [Fact]
    public async Task GenerateCommentaryAsync_ThrowsCleanMessage_WhenNoCandidatesAndNoBlockReason()
    {
        var json = "{}";
        var client = BuildClient(new StubHandler(json));

        var act = () => client.GenerateCommentaryAsync("Title", "Artist", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateCommentaryAsync_Throws_WhenCandidateFinishReasonIsSafety()
    {
        // A 200 with a candidate whose finishReason is SAFETY means no usable text.
        var json = @"{""candidates"":[{""content"":{""parts"":[{""text"":""""}]},""finishReason"":""SAFETY""}]}";
        var client = BuildClient(new StubHandler(json));

        var act = () => client.GenerateCommentaryAsync("Title", "Artist", CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*finishReason*SAFETY*");
    }
}
