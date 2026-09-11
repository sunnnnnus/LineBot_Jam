using System.Text.Json;

namespace Linebot_jam.Services;

public class AiResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? FunctionName { get; init; }
    public JsonElement? FunctionArgs { get; init; }

    public static AiResult Failure() => new() { Success = false };
}
