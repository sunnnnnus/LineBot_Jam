using System.Net.Http.Json;

namespace Linebot_jam.Services;

public class LineMessagingClient : ILineMessagingClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LineMessagingClient> _logger;

    public LineMessagingClient(HttpClient httpClient, ILogger<LineMessagingClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task ReplyMessageAsync(string replyToken, string text, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            replyToken,
            messages = new[] { new { type = "text", text } }
        };

        using var response = await _httpClient.PostAsJsonAsync("v2/bot/message/reply", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("LINE Reply API call failed: {StatusCode} {Body}", response.StatusCode, body);
        }
    }

    public async Task<bool> PushMessageAsync(string userId, string text, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            to = userId,
            messages = new[] { new { type = "text", text } }
        };

        using var response = await _httpClient.PostAsJsonAsync("v2/bot/message/push", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("LINE Push API call failed: {StatusCode} {Body}", response.StatusCode, body);
            return false;
        }

        return true;
    }
}
