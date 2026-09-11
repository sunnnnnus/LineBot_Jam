using System.Text.Json;
using Linebot_jam.Models.Line;
using Linebot_jam.Services;
using Microsoft.AspNetCore.Mvc;

namespace Linebot_jam.Controllers;

[ApiController]
[Route("api/line/webhook")]
public class LineWebhookController(ILineSignatureValidator signatureValidator,
    IWebhookQueue queue, ILogger<LineWebhookController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        // Verify the exact bytes LINE signed; do not decode/re-encode before HMAC.
        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, ct);
        if (!signatureValidator.IsValidSignature(body.ToArray(), Request.Headers["X-Line-Signature"]))
            return Unauthorized();

        LineWebhookRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LineWebhookRequest>(body.ToArray(), JsonOptions);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        var events = payload?.Events?.Where(evt => evt.Type == "message" &&
            evt.Message?.Type == "text" && !string.IsNullOrEmpty(evt.ReplyToken)).ToList();
        // LINE's Verify request contains no events. No database or AI round trip required.
        if (events is null || events.Count == 0) return Ok();

        try
        {
            foreach (var evt in events) WebhookQueue.GetEventId(evt);
        }
        catch (ArgumentException) { return BadRequest(); }

        try
        {
            await queue.EnqueueAsync(events, ct);
            return Ok(); // Only acknowledge events that are durably stored.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Webhook could not be persisted; requesting LINE redelivery.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
