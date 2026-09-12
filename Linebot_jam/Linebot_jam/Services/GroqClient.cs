using System.Net.Http.Json;
using System.Text.Json;
using Linebot_jam.Options;
using Microsoft.Extensions.Options;

namespace Linebot_jam.Services;

public class GroqClient : IAiClient
{
    private static readonly object[] Tools =
    {
        new
        {
            type = "function",
            function = new
            {
                name = "create_tasks",
                description = "當使用者的訊息包含足夠資訊(要做的事、以及明確的到期日期時間)可以新增待辦提醒時呼叫此函式。" +
                              "使用者一則訊息裡可能列了多件待辦(例如 1. 2. 3. 條列、頓號或換行分隔)," +
                              "請把每一件都放進 tasks 陣列裡一次傳回,不要只取第一件、也不要把多件合併成一筆。",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        tasks = new
                        {
                            type = "array",
                            description = "要新增的待辦事項清單,一件一個元素",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    content = new { type = "string", description = "待辦事項的簡短內容" },
                                    due_at = new { type = "string", description = "到期時間,ISO 8601 格式,例如 2026-08-20T18:00:00" }
                                },
                                required = new[] { "content", "due_at" }
                            }
                        }
                    },
                    required = new[] { "tasks" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "ask_clarification",
                description = "當使用者的訊息看起來想新增待辦提醒,但缺少必要資訊(例如沒有講明確時間)時呼叫此函式,提出一個問題來追問。",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        question = new { type = "string", description = "要反問使用者的問題" }
                    },
                    required = new[] { "question" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "confirm_task",
                description = "當目前有一個等待使用者確認的待辦提議,且使用者的回覆表示同意、確定要新增(不論用什麼說法)時呼叫此函式。",
                parameters = new
                {
                    type = "object",
                    properties = new { }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "cancel_task",
                description = "當目前有一個等待使用者確認的待辦提議,且使用者的回覆表示不要、取消(不論用什麼說法)時呼叫此函式。",
                parameters = new
                {
                    type = "object",
                    properties = new { }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "complete_tasks",
                description = "當使用者表示某些既有待辦事項已經做完時呼叫此函式(例如「倒垃圾好了」、「第2件完成」、「開會跟繳費都done了」)。" +
                              "task_ids 請填上方待辦清單中對應項目的編號,一次可以填多筆。" +
                              "只能填清單裡真的存在的編號,不確定是哪一筆時不要猜,改用文字回覆詢問使用者。",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        task_ids = new
                        {
                            type = "array",
                            description = "要標記為完成的待辦編號",
                            items = new { type = "integer" }
                        }
                    },
                    required = new[] { "task_ids" }
                }
            }
        }
    };

    private readonly HttpClient _httpClient;
    private readonly GroqOptions _options;
    private readonly ILogger<GroqClient> _logger;

    public GroqClient(HttpClient httpClient, IOptions<GroqOptions> options, ILogger<GroqClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiResult> GenerateAsync(string userInput, string systemInstruction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            _logger.LogWarning("Groq API key not configured.");
            return AiResult.Failure();
        }

        var payload = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = systemInstruction },
                new { role = "user", content = userInput }
            },
            tools = Tools
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "openai/v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Groq API request failed.");
            return AiResult.Failure();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Groq API call failed: {StatusCode} {Body}", response.StatusCode, body);
                return AiResult.Failure();
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var message = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message");

                if (message.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind == JsonValueKind.Array
                    && toolCalls.GetArrayLength() > 0)
                {
                    var calls = new List<AiFunctionCall>();

                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var function = toolCall.GetProperty("function");
                        var name = function.GetProperty("name").GetString() ?? string.Empty;
                        var argsJson = function.GetProperty("arguments").GetString();

                        JsonElement? args = null;
                        if (!string.IsNullOrEmpty(argsJson))
                        {
                            using var argsDoc = JsonDocument.Parse(argsJson);
                            args = argsDoc.RootElement.Clone();
                        }

                        calls.Add(new AiFunctionCall { Name = name, Args = args });

                        // 排查用:模型實際回了什麼工具、參數長什麼樣(多筆待辦是否都有進 tasks 陣列)
                        _logger.LogInformation("Groq tool call: {Name} {Arguments}", name, argsJson);
                    }

                    return new AiResult { Success = true, FunctionCalls = calls };
                }

                var text = message.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
                return new AiResult { Success = true, Text = text };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Groq response.");
                return AiResult.Failure();
            }
        }
    }
}
