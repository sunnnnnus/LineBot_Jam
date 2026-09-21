using System.Text.Json;

namespace Linebot_jam.Services;

// Scoped to one event. No HTTP is performed inside the task database transaction.
public class PendingLineReply
{
    public string? MessagesJson { get; private set; }
    public void SetMessages(IEnumerable<object> messages) => MessagesJson = JsonSerializer.Serialize(messages);

    public Task ReplyMessageAsync(string replyToken, string text)
    {
        // Leave space below LINE's text limit; never split a surrogate pair.
        if (text.Length > 4900)
        {
            var length = char.IsHighSurrogate(text[4899]) ? 4899 : 4900;
            text = text[..length] + "\n（內容過長，已省略部分文字）";
        }
        MessagesJson = JsonSerializer.Serialize(new[] { new { type = "text", text } });
        return Task.CompletedTask;
    }
}
