using System.Text.Json;

namespace Linebot_jam.Services;

public class AiResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }

    /// 模型可能一次回傳多個工具呼叫(例如使用者一則訊息列了好幾件待辦),全部保留。
    public IReadOnlyList<AiFunctionCall> FunctionCalls { get; init; } = Array.Empty<AiFunctionCall>();

    public static AiResult Failure() => new() { Success = false };
}

public class AiFunctionCall
{
    public string Name { get; init; } = string.Empty;
    public JsonElement? Args { get; init; }
}
