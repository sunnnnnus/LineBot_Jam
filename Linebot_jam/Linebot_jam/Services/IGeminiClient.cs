namespace Linebot_jam.Services;

public interface IGeminiClient
{
    Task<string?> GenerateReplyAsync(string userMessage, string context, CancellationToken cancellationToken = default);
}
