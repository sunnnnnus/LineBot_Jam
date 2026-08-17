using System.Text.Json;

namespace Linebot_jam.Services;

public class GeminiResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? FunctionName { get; init; }
    public JsonElement? FunctionArgs { get; init; }

    public static GeminiResult Failure() => new() { Success = false };
}
