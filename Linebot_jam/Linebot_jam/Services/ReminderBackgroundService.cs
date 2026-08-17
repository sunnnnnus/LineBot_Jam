using Linebot_jam.Data;
using Linebot_jam.Models;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services;

public class ReminderBackgroundService : BackgroundService
{
    private static readonly (string Type, TimeSpan LeadTime, string Label)[] Stages =
    {
        ("3d_before", TimeSpan.FromDays(3), "還有 3 天到期"),
        ("1d_before", TimeSpan.FromDays(1), "還有 1 天到期"),
        ("3h_before", TimeSpan.FromHours(3), "還有 3 小時到期"),
        ("due", TimeSpan.Zero, "已到期")
    };

    private static readonly TimeSpan MaxLeadTime = Stages[0].LeadTime;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReminderBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public ReminderBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReminderBackgroundService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<int?>("Reminder:IntervalMinutes") ?? 60;
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await ScanAndSendRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder scan failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ScanAndSendRemindersAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lineClient = scope.ServiceProvider.GetRequiredService<ILineMessagingClient>();

        var now = DateTime.Now;
        var candidates = await db.Tasks
            .Include(t => t.User)
            .Where(t => t.Status == "pending" && t.DueAt <= now + MaxLeadTime)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return;

        foreach (var task in candidates)
        {
            var timeUntilDue = task.DueAt - now;

            foreach (var (type, leadTime, label) in Stages)
            {
                if (timeUntilDue > leadTime)
                    continue;

                var alreadySent = await db.ReminderLogs
                    .AnyAsync(r => r.TaskId == task.Id && r.ReminderType == type, ct);
                if (alreadySent)
                    continue;

                try
                {
                    var sent = await lineClient.PushMessageAsync(
                        task.User.LineUserId,
                        $"提醒: {task.Content} {label}({task.DueAt:yyyy/MM/dd HH:mm})",
                        ct);

                    if (sent)
                    {
                        db.ReminderLogs.Add(new ReminderLog
                        {
                            TaskId = task.Id,
                            ReminderType = type,
                            Channel = "LINE"
                        });
                        await db.SaveChangesAsync(ct);
                    }
                    else
                    {
                        _logger.LogWarning("Push failed for task {TaskId} stage {Stage}; will retry on next scan.", task.Id, type);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send {Stage} reminder for task {TaskId}.", type, task.Id);
                }
            }
        }
    }
}
