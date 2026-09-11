namespace Linebot_jam.Services;

public interface IAiClient
{
    Task<AiResult> GenerateAsync(string userInput, string systemInstruction, CancellationToken cancellationToken = default);
}
