using System.Net.Http.Json;
using System.Text.Json;
using Linebot_jam.Options;
using Microsoft.Extensions.Options;

namespace Linebot_jam.Services;

public class GeminiClient : IGeminiClient
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(HttpClient httpClient, IOptions<GeminiOptions> options, ILogger<GeminiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GenerateReplyAsync(string userMessage, string context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            _logger.LogWarning("Gemini API key not configured; skipping AI reply.");
            return null;
        }

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = context } }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = userMessage } } }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{_options.Model}:generateContent")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Gemini API call failed: {StatusCode} {Body}", response.StatusCode, body);
            return null;
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini response.");
            return null;
        }
    }
}
