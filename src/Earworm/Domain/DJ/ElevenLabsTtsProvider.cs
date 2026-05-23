using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Earworm.Config;

namespace Earworm.Domain.DJ;

public sealed class ElevenLabsTtsProvider : ITtsProvider
{
    private readonly HttpClient _httpClient;
    private readonly EarwormConfig _config;
    private readonly ILogger<ElevenLabsTtsProvider> _logger;

    public ElevenLabsTtsProvider(HttpClient httpClient, EarwormConfig config, ILogger<ElevenLabsTtsProvider> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<Stream> RenderTtsAsync(string text, CancellationToken cancellationToken)
    {
        string apiKey = Environment.GetEnvironmentVariable("EARWORM_ELEVENLABS_API_KEY") ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("EARWORM_ELEVENLABS_API_KEY environment variable is not set.");
            throw new InvalidOperationException("ElevenLabs API key is missing. Set the EARWORM_ELEVENLABS_API_KEY environment variable.");
        }

        string voiceId = _config.Dj.Tts.VoiceId;
        if (string.IsNullOrEmpty(voiceId))
        {
            _logger.LogError("ElevenLabs VoiceId is not configured.");
            throw new InvalidOperationException("ElevenLabs VoiceId is missing in the configuration.");
        }

        string endpoint = $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}";
        _logger.LogInformation("Sending TTS request to ElevenLabs for text: '{Text}'", text);

        var requestBody = new
        {
            text = text,
            model_id = _config.Dj.Tts.ModelId,
            voice_settings = new
            {
                stability = _config.Dj.Tts.Stability,
                similarity_boost = _config.Dj.Tts.SimilarityBoost
            }
        };

        string json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("xi-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("ElevenLabs API error: {StatusCode} - {Content}", response.StatusCode, errContent);
            throw new HttpRequestException($"ElevenLabs returned status code {response.StatusCode}: {errContent}");
        }

        _logger.LogInformation("TTS successfully rendered from ElevenLabs.");
        // Buffer into memory so the HttpResponseMessage can be disposed before
        // returning. TTS clips are short (tens to a few hundred KB), so the
        // memory cost is trivial compared to leaking the HTTP connection.
        var buffered = new MemoryStream();
        await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await src.CopyToAsync(buffered, cancellationToken);
        }
        buffered.Position = 0;
        return buffered;
    }
}
