using System.Net.Http.Json;
using System.Text.Json;
using Linebot_jam.Options;
using Microsoft.Extensions.Options;

namespace Linebot_jam.Services;

public class GeminiClient : IGeminiClient
{
    private static readonly object Tools = new object[]
    {
        new
        {
            functionDeclarations = new object[]
            {
                new
                {
                    name = "create_task",
                    description = "當使用者的訊息包含足夠資訊(要做的事、以及明確的到期日期時間)可以新增一筆待辦提醒時呼叫此函式。",
                    parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            content = new { type = "STRING", description = "待辦事項的簡短內容" },
                            due_at = new { type = "STRING", description = "到期時間,ISO 8601 格式,例如 2026-08-20T18:00:00" }
                        },
                        required = new[] { "content", "due_at" }
                    }
                },
                new
                {
                    name = "ask_clarification",
                    description = "當使用者的訊息看起來想新增待辦提醒,但缺少必要資訊(例如沒有講明確時間)時呼叫此函式,提出一個問題來追問。",
                    parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            question = new { type = "STRING", description = "要反問使用者的問題" }
                        },
                        required = new[] { "question" }
                    }
                },
                new
                {
                    name = "confirm_task",
                    description = "當目前有一個等待使用者確認的待辦提議,且使用者的回覆表示同意、確定要新增(不論用什麼說法)時呼叫此函式。",
                    parameters = new
                    {
                        type = "OBJECT",
                        properties = new { }
                    }
                },
                new
                {
                    name = "cancel_task",
                    description = "當目前有一個等待使用者確認的待辦提議,且使用者的回覆表示不要、取消(不論用什麼說法)時呼叫此函式。",
                    parameters = new
                    {
                        type = "OBJECT",
                        properties = new { }
                    }
                }
            }
        },
        new
        {
            googleSearch = new { }
        }
    };

    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(HttpClient httpClient, IOptions<GeminiOptions> options, ILogger<GeminiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GeminiResult> GenerateAsync(string userInput, string systemInstruction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            _logger.LogWarning("Gemini API key not configured.");
            return GeminiResult.Failure();
        }

        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = systemInstruction } } },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = userInput } } }
            },
            tools = Tools
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{_options.Model}:generateContent")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini API request failed.");
            return GeminiResult.Failure();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Gemini API call failed: {StatusCode} {Body}", response.StatusCode, body);
                return GeminiResult.Failure();
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var parts = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts");

                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("functionCall", out var functionCall))
                    {
                        var name = functionCall.GetProperty("name").GetString();
                        var args = functionCall.TryGetProperty("args", out var argsElement)
                            ? argsElement.Clone()
                            : (JsonElement?)null;

                        return new GeminiResult { Success = true, FunctionName = name, FunctionArgs = args };
                    }
                }

                var text = string.Concat(parts.EnumerateArray()
                    .Where(p => p.TryGetProperty("text", out _))
                    .Select(p => p.GetProperty("text").GetString()));

                return new GeminiResult { Success = true, Text = text };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Gemini response.");
                return GeminiResult.Failure();
            }
        }
    }
}
