using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services;

public class ReminderProcessor(IServiceScopeFactory scopes, IConfiguration configuration)
{
    public const string BatchPrefix = "reminder-batch:";

    public async Task<int> QueueDueRemindersAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74129004)", ct);
        var now = DateTime.Now;
        var limit = now.AddDays(3);
        var tasks = await db.Tasks.AsNoTracking()
            .Include(t => t.User).Include(t => t.ReminderLogs)
            .Where(t => t.Status == "pending" && t.DueAt <= limit)
            .ToListAsync(ct);
        var ids = tasks.Select(t => t.Id).ToArray();
        var reserved = (await db.ReminderDispatches.Where(d => ids.Contains(d.TaskId)).ToListAsync(ct))
            .Select(d => (d.TaskId, d.ReminderType)).ToHashSet();
        var candidates = tasks.Select(t => new ReminderCandidate(t, ReminderStageSelector.Select(t.DueAt - now)!))
            .Where(c => !c.Task.ReminderLogs.Any(r => r.ReminderType == c.Stage) &&
                !reserved.Contains((c.Task.Id, c.Stage)));
        var window = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue<int?>("Reminder:MergeWindowMinutes") ?? 30, 0, 1440));
        var batches = ReminderBatchBuilder.Group(candidates, window);
        foreach (var batch in batches)
        {
            var utcNow = DateTime.UtcNow;
            var job = new WebhookJob
            {
                EventId = BatchPrefix + Guid.NewGuid().ToString("N"), Payload = "{}",
                ReceivedAt = utcNow, NextAttemptAt = utcNow, Processed = true, UsePush = true,
                Destination = batch[0].Task.User.LineUserId,
                ReplyMessages = JsonSerializer.Serialize(new[] { new { type = "text", text = ReminderBatchBuilder.Format(batch, now) } })
            };
            db.WebhookJobs.Add(job);
            db.ReminderDispatches.AddRange(batch.Select(c => new ReminderDispatch
            {
                TaskId = c.Task.Id, ReminderType = c.Stage, EventId = job.EventId
            }));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return batches.Count;
    }
}
