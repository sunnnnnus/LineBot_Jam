using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Controllers;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services;

public class WebhookBackgroundService(IServiceScopeFactory scopes,
    ILogger<WebhookBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before initialization so a slow database cannot block the HTTP listener.
        await Task.Yield();
        var initialized = false;
        var nextCleanup = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74129001)", stoppingToken);
                    using var stream = typeof(AppDbContext).Assembly.GetManifestResourceStream("Linebot_jam.Data.WebhookQueue.sql")!;
                    using var reader = new StreamReader(stream);
                    await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(stoppingToken), stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                    initialized = true;
                    logger.LogInformation("Durable webhook queue ready.");
                }

                var processed = await ProcessNextAsync(stoppingToken);
                var delivered = await DeliverNextAsync(stoppingToken);
                if (DateTime.UtcNow >= nextCleanup)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var cutoff = DateTime.UtcNow.AddDays(-7);
                    await db.WebhookJobs.Where(j => j.Finished && j.ReceivedAt < cutoff &&
                        (j.LastError == null || !j.EventId.StartsWith(ReminderProcessor.BatchPrefix)))
                        .ExecuteDeleteAsync(stoppingToken);
                    nextCleanup = DateTime.UtcNow.AddHours(1);
                }
                if (!processed && !delivered)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook worker unavailable; retrying in 5 seconds.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize task mutations across overlapping Render deploys, preserving inbox order.
        if (!await TryLockAsync(db, 74129002, ct)) return false;
        var job = await db.WebhookJobs.Where(j => !j.Processed)
            .OrderBy(j => j.Sequence).FirstOrDefaultAsync(ct);
        if (job is null || job.NextAttemptAt > DateTime.UtcNow) return false;

        var reply = scope.ServiceProvider.GetRequiredService<PendingLineReply>();
        try
        {
            var evt = JsonSerializer.Deserialize<LineEvent>(job.Payload)!;
            job.Destination = evt.Source?.PushDestination;
            // Old events should not silently interpret "tomorrow" relative to a later day.
            if (evt.Type == ReminderController.TriggerType)
                await scope.ServiceProvider.GetRequiredService<ReminderProcessor>().QueueDueRemindersAsync(ct);
            else if (evt.Timestamp > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - evt.Timestamp > 10 * 60 * 1000)
                await reply.ReplyMessageAsync(evt.ReplyToken!, "服務剛恢復，這則訊息已超過 10 分鐘，尚未執行。請重新傳送要處理的事項。");
            else
                await scope.ServiceProvider.GetRequiredService<LineEventProcessor>().HandleTextMessageAsync(evt);

            job.ReplyMessages = reply.MessagesJson;
            job.Processed = true;
            job.Finished = job.ReplyMessages is null;
            job.NextAttemptAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // Task inserts, pending-state changes and the outbox are atomic.
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            logger.LogError(ex, "Webhook processing failed for {EventId}; task transaction rolled back.", job.EventId);
            // Use a new locked transaction; never accidentally commit tracked task inserts.
            await using var retryTransaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74129002)", ct);
            var retry = await db.WebhookJobs.SingleAsync(j => j.EventId == job.EventId, ct);
            if (!retry.Processed)
            {
                retry.ProcessingAttempts++;
                retry.LastError = "Processing failed; see server logs.";
                retry.NextAttemptAt = DateTime.UtcNow.AddSeconds(5);
                if (retry.ProcessingAttempts >= 3)
                {
                    var failedEvent = JsonSerializer.Deserialize<LineEvent>(retry.Payload);
                    if (failedEvent?.Type == ReminderController.TriggerType)
                        retry.Finished = true; // Keep LastError for operations; the next scheduler run can retry.
                    else
                    {
                        await reply.ReplyMessageAsync("", "服務暫時無法處理，這次沒有新增任務。請稍後重新傳送。");
                        retry.ReplyMessages = reply.MessagesJson;
                        retry.Destination = failedEvent?.Source?.PushDestination;
                    }
                    retry.Processed = true;
                }
                await db.SaveChangesAsync(ct);
            }
            await retryTransaction.CommitAsync(ct);
            return true;
        }
    }

    public async Task<bool> DeliverNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await TryLockAsync(db, 74129003, ct)) return false;
        var now = DateTime.UtcNow;
        var job = await db.WebhookJobs.Where(j => j.Processed && !j.Finished && j.NextAttemptAt <= now)
            .OrderBy(j => j.Sequence).FirstOrDefaultAsync(ct);
        if (job is null) return false;

        if (job.ReplyAttempted && !job.UsePush)
        {
            // Previous process died after reserving a reply; sending again could duplicate it.
            job.Finished = true;
            job.LastError = "Reply outcome unknown after restart; not resent.";
        }
        else if (job.UsePush && (string.IsNullOrEmpty(job.Destination) ||
                 job.PushStartedAt < now.AddHours(-23)))
        {
            job.Finished = true;
            job.LastError = "Push destination missing or retry window expired.";
        }
        else
        {
            job.ReplyAttempted = true;
            if (job.UsePush) job.PushStartedAt ??= now;
            job.DeliveryAttempts++;
            // Persist the attempt before HTTP. The reservation spans transaction boundaries.
            job.NextAttemptAt = now.AddMinutes(1);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await using var deliveryTransaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74129003)", ct);
            var attempt = job.DeliveryAttempts;
            await db.Entry(job).ReloadAsync(ct);
            if (job.Finished || job.DeliveryAttempts != attempt || job.NextAttemptAt <= DateTime.UtcNow)
                return true; // Reservation expired or was handled by another worker.
            var result = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryClient>().SendAsync(job, ct);
            ApplyDeliveryResult(job, result, DateTime.UtcNow);
            if (result == DeliveryResult.Accepted && job.EventId.StartsWith(ReminderProcessor.BatchPrefix))
            {
                var dispatched = await db.ReminderDispatches.Where(d => d.EventId == job.EventId).ToListAsync(ct);
                foreach (var item in dispatched)
                {
                    if (!await db.ReminderLogs.AnyAsync(r => r.TaskId == item.TaskId && r.ReminderType == item.ReminderType, ct))
                        db.ReminderLogs.Add(new ReminderLog { TaskId = item.TaskId, ReminderType = item.ReminderType, Channel = "LINE" });
                }
            }
            await db.SaveChangesAsync(ct);
            await deliveryTransaction.CommitAsync(ct);
            LogDelivery(job);
            return true;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        LogDelivery(job);
        return true;
    }

    public static void ApplyDeliveryResult(WebhookJob job, DeliveryResult result, DateTime now)
    {
        if (result == DeliveryResult.InvalidReplyToken && !job.UsePush && !string.IsNullOrEmpty(job.Destination))
        {
            job.UsePush = true;
            job.NextAttemptAt = now;
            job.LastError = null;
        }
        else if (result == DeliveryResult.Retryable && job.UsePush && job.DeliveryAttempts < 6)
        {
            job.NextAttemptAt = now.AddSeconds(Math.Pow(2, job.DeliveryAttempts));
            job.LastError = "Push temporarily unavailable; retry scheduled.";
        }
        else
        {
            job.Finished = true;
            job.LastError = result == DeliveryResult.Accepted ? null : $"Delivery stopped: {result}.";
        }
    }

    private void LogDelivery(WebhookJob job)
    {
        if (job.LastError is not null)
            logger.LogWarning("Webhook {EventId}: {Error}", job.EventId, job.LastError);
    }

    private static Task<bool> TryLockAsync(AppDbContext db, int key, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(ct);
}
