using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;

namespace Linebot_jam.Services;

public enum DeliveryResult { Accepted, InvalidReplyToken, Retryable, Rejected, Ambiguous }

public class WebhookDeliveryClient(HttpClient httpClient, ILogger<WebhookDeliveryClient> logger)
{
    public async Task<DeliveryResult> SendAsync(WebhookJob job, CancellationToken ct)
    {
        var evt = JsonSerializer.Deserialize<LineEvent>(job.Payload)!;
        using var messages = JsonDocument.Parse(job.ReplyMessages!);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            job.UsePush ? "v2/bot/message/push" : "v2/bot/message/reply");
        request.Content = job.UsePush
            ? JsonContent.Create(new { to = job.Destination, messages = messages.RootElement })
            : JsonContent.Create(new { replyToken = evt.ReplyToken, messages = messages.RootElement });
        if (job.UsePush)
            request.Headers.Add("X-Line-Retry-Key", job.RetryKey.ToString());

        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode ||
                (job.UsePush && response.StatusCode == HttpStatusCode.Conflict &&
                 response.Headers.Contains("x-line-accepted-request-id")))
                return DeliveryResult.Accepted;

            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("LINE delivery {EventId} returned {StatusCode} (push={Push}).",
                job.EventId, (int)response.StatusCode, job.UsePush);
            if (!job.UsePush && response.StatusCode == HttpStatusCode.BadRequest && IsInvalidReplyToken(body))
                return DeliveryResult.InvalidReplyToken;

            if ((int)response.StatusCode >= 500)
                return job.UsePush ? DeliveryResult.Retryable : DeliveryResult.Ambiguous;
            return DeliveryResult.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "LINE delivery transport failure for {EventId}.", job.EventId);
            // Reply has no retry key: a timeout might already have delivered the message.
            return job.UsePush ? DeliveryResult.Retryable : DeliveryResult.Ambiguous;
        }
    }

    private static bool IsInvalidReplyToken(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                string.Equals(message.GetString(), "Invalid reply token", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }
}
