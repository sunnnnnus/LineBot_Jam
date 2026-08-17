namespace Linebot_jam.Services;

public interface IGeminiClient
{
    Task<GeminiResult> GenerateAsync(string userInput, string systemInstruction, CancellationToken cancellationToken = default);
}
