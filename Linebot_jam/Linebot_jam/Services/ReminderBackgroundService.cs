namespace Linebot_jam.Services;

// Optional local timer. External scheduling disables this; delivery still drains durable jobs.
public class ReminderBackgroundService(ReminderProcessor processor,
    ILogger<ReminderBackgroundService> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var minutes = Math.Max(1, configuration.GetValue<int?>("Reminder:IntervalMinutes") ?? 30);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try { await processor.QueueDueRemindersAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Reminder scan failed; retry on next trigger."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
