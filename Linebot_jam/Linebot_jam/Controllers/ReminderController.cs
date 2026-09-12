using System.Security.Cryptography;
using System.Text;
using Linebot_jam.Models.Line;
using Linebot_jam.Services;
using Microsoft.AspNetCore.Mvc;

namespace Linebot_jam.Controllers;

[ApiController]
[Route("api/reminder")]
public class ReminderController(IWebhookQueue queue, IConfiguration configuration,
    TimeProvider clock, ILogger<ReminderController> logger) : ControllerBase
{
    public const string TriggerType = "reminder_scan";

    [HttpPost("process")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Process(CancellationToken ct)
    {
        var secret = configuration["Reminder:SchedulerToken"];
        if (string.IsNullOrWhiteSpace(secret)) return StatusCode(503, new { status = "scheduler_not_configured" });
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
            return Unauthorized();

        // Scheduler retries use the same key, including when a retry crosses a half-hour boundary.
        var key = Request.Headers["X-Scheduler-Run-Id"].ToString();
        if (key.Length > 0 && !Guid.TryParseExact(key, "D", out _)) return BadRequest();
        var now = clock.GetUtcNow();
        var id = "reminder-scan:" + (key.Length > 0 ? key.ToLowerInvariant() : (now.ToUnixTimeSeconds() / 1800).ToString());
        try
        {
            await queue.EnqueueAsync(new[] { new LineEvent
            {
                WebhookEventId = id, Type = TriggerType, Timestamp = now.ToUnixTimeMilliseconds()
            } }, ct);
            // This acknowledges persistence, not completed LINE delivery.
            return Accepted(new { status = "accepted", requestId = id });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not persist reminder trigger.");
            return StatusCode(503, new { status = "retry_later" });
        }
    }
}
