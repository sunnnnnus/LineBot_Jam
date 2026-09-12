using Linebot_jam.Data;
using Linebot_jam.Models;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services;

public class ReminderBackgroundService : BackgroundService
{
    private static readonly TimeSpan MaxLeadTime = TimeSpan.FromDays(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReminderBackgroundService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _mergeWindow;

    public ReminderBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReminderBackgroundService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<int?>("Reminder:IntervalMinutes") ?? 1;
        _interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _mergeWindow = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue<int?>("Reminder:MergeWindowMinutes") ?? 30, 0, 1440));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await ScanAndSendRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder scan failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ScanAndSendRemindersAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lineClient = scope.ServiceProvider.GetRequiredService<ILineMessagingClient>();
        var now = DateTime.Now;
        var tasks = await db.Tasks.AsNoTracking()
            .Include(t => t.User).Include(t => t.ReminderLogs)
            .Where(t => t.Status == "pending" && t.DueAt <= now + MaxLeadTime)
            .ToListAsync(ct);
        var candidates = tasks.Select(t => new ReminderCandidate(t, ReminderStageSelector.Select(t.DueAt - now)!))
            .Where(c => !c.Task.ReminderLogs.Any(r => r.ReminderType == c.Stage));

        foreach (var batch in ReminderBatchBuilder.Group(candidates, _mergeWindow))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Overlapping deployments must recheck logs after acquiring the sender lock.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(74129004)", ct);
            var ids = batch.Select(c => c.Task.Id).ToArray();
            var stage = batch[0].Stage;
            var sentIds = await db.ReminderLogs.Where(r => ids.Contains(r.TaskId) && r.ReminderType == stage)
                .Select(r => r.TaskId).ToListAsync(ct);
            var unsent = batch.Where(c => !sentIds.Contains(c.Task.Id)).ToList();
            if (unsent.Count == 0) continue;

            // Save one log per task only when the entire message was accepted.
            var sent = await lineClient.PushMessageAsync(unsent[0].Task.User.LineUserId,
                ReminderBatchBuilder.Format(unsent, now), ct);
            if (!sent)
            {
                _logger.LogWarning("Reminder batch failed for {Count} tasks at {Stage}; retry on next scan.", unsent.Count, stage);
                continue;
            }
            db.ReminderLogs.AddRange(unsent.Select(c => new ReminderLog
            {
                TaskId = c.Task.Id, ReminderType = c.Stage, Channel = "LINE"
            }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
    }
}
