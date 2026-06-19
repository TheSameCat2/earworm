using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Earworm.Config;

namespace Earworm.Domain.DJ;

public class GeminiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EarwormConfig _config;
    private readonly ILogger<GeminiClient> _logger;
    private readonly string _apiKey;

    public GeminiClient(IHttpClientFactory httpClientFactory, EarwormConfig config, ILogger<GeminiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;

        string apiKey = Environment.GetEnvironmentVariable("EARWORM_GEMINI_API_KEY") ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Gemini API key is missing. Set the EARWORM_GEMINI_API_KEY environment variable.");
        _apiKey = apiKey;
    }

    public virtual async Task<string> GenerateCommentaryAsync(string trackTitle, string trackArtist, CancellationToken cancellationToken)
    {
        using var _httpClient = _httpClientFactory.CreateClient(nameof(GeminiClient));

        string model = _config.Dj.GeminiModel;
        string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        // Format prompt using the configured persona
        string trackMetadata = $"'{trackTitle}' by '{trackArtist}'";
        string prompt = _config.Dj.PersonaPrompt.Replace("{track_metadata}", trackMetadata);

        _logger.LogInformation("Generating DJ commentary via Gemini model {Model} for track: {Title}", model, trackTitle);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Goog-Api-Key", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Gemini API error: {StatusCode} - {Content}", response.StatusCode, errContent);
            throw new HttpRequestException($"Gemini returned status code {response.StatusCode}: {errContent}");
        }

        string resJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(resJson);

        // Gemini can return a 200 with no candidates — e.g. when the prompt is
        // blocked by safety filters or the response is empty. Previously this
        // threw an opaque index-out-of-range on `candidates[0]`; surface a
        // clear, logged reason instead so the DJ caller can skip cleanly.
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            string? blockReason = null;
            if (doc.RootElement.TryGetProperty("promptFeedback", out var feedback) &&
                feedback.TryGetProperty("blockReason", out var reason))
            {
                blockReason = reason.GetString();
            }

            _logger.LogWarning(
                "Gemini returned no candidates (blockReason={BlockReason}); skipping commentary. JSON: {Json}",
                blockReason ?? "none", resJson);
            throw new InvalidOperationException(
                "Gemini returned no commentary candidates" +
                (blockReason != null ? $" (blockReason: {blockReason})" : string.Empty) + ".");
        }

        // A candidate may also be blocked at the per-candidate level (finishReason = SAFETY).
        var candidate = candidates[0];
        if (candidate.TryGetProperty("finishReason", out var finishReason))
        {
            var reasonText = finishReason.GetString();
            if (!string.Equals(reasonText, "STOP", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(reasonText, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Gemini candidate finishReason={FinishReason}; skipping commentary. JSON: {Json}",
                    reasonText, resJson);
                throw new InvalidOperationException(
                    $"Gemini commentary was not returned (finishReason: {reasonText}).");
            }
        }

        try
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0 ||
                !parts[0].TryGetProperty("text", out var textProp))
            {
                throw new InvalidOperationException("Gemini returned an empty/incomplete content payload.");
            }

            var text = textProp.GetString();

            if (string.IsNullOrEmpty(text))
            {
                throw new InvalidOperationException("Gemini returned empty text response.");
            }

            string commentary = text.Trim();
            _logger.LogInformation("Successfully generated commentary: '{Commentary}'", commentary);
            return commentary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini API response. JSON: {Json}", resJson);
            throw new InvalidOperationException("Failed to parse response from Gemini API.", ex);
        }
    }
}
