using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models.Line;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services;

public interface IWebhookQueue
{
    Task EnqueueAsync(IEnumerable<LineEvent> events, CancellationToken ct);
}

public class WebhookQueue(AppDbContext db) : IWebhookQueue
{
    public async Task EnqueueAsync(IEnumerable<LineEvent> events, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var evt in events)
        {
            var eventId = GetEventId(evt);
            var payload = JsonSerializer.Serialize(evt);
            var now = DateTime.UtcNow;
            var retryKey = Guid.NewGuid();
            // Never overwrite a processed event or re-run business changes on redelivery.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WEBHOOK_JOBS"
                    ("EventId", "Payload", "ReceivedAt", "NextAttemptAt", "RetryKey")
                VALUES ({eventId}, {payload}, {now}, {now}, {retryKey})
                ON CONFLICT ("EventId") DO NOTHING
                """, ct);
        }
        await transaction.CommitAsync(ct);
    }

    public static string GetEventId(LineEvent evt) =>
        !string.IsNullOrWhiteSpace(evt.WebhookEventId) ? evt.WebhookEventId :
        !string.IsNullOrWhiteSpace(evt.Message?.Id) ? "message:" + evt.Message.Id :
        throw new ArgumentException("A message event must have webhookEventId or message.id.");
}
