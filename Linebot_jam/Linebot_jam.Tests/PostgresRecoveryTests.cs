using System.Net;
using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Linebot_jam.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace Linebot_jam.Tests;

// CI supplies a disposable PostgreSQL service. Never point this at a production database.
[TestClass]
public class PostgresRecoveryTests
{
    private string _baseConnection = "";
    private string _schema = "";
    private ServiceProvider? _services;
    private readonly FailOutboxOnce _failure = new();
    private readonly RecordingLineClient _line = new();

    [TestInitialize]
    public async Task Initialize()
    {
        _baseConnection = Environment.GetEnvironmentVariable("LINEBOT_TEST_POSTGRES") ?? "";
        if (_baseConnection.Length == 0)
            Assert.Inconclusive("Set LINEBOT_TEST_POSTGRES to run real PostgreSQL recovery tests (enabled in CI).");
        _schema = "test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_baseConnection);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {_schema}", connection);
        await create.ExecuteNonQueryAsync();

        var scopedConnection = new NpgsqlConnectionStringBuilder(_baseConnection) { SearchPath = _schema }.ConnectionString;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(scopedConnection).AddInterceptors(_failure));
        services.AddScoped<PendingLineReply>();
        services.AddScoped<LineEventProcessor>();
        services.AddSingleton<IAiClient, FakeAi>();
        services.AddSingleton<ILineMessagingClient>(_line);
        services.AddSingleton(ColdStartTests.Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // This is a script, not an EF format string (SQL comments contain JSON braces).
        await using var setupConnection = new NpgsqlConnection(scopedConnection);
        await setupConnection.OpenAsync();
        await using var setup = new NpgsqlCommand(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "CreateTable.sql")), setupConnection);
        await setup.ExecuteNonQueryAsync();
        using var stream = typeof(AppDbContext).Assembly.GetManifestResourceStream("Linebot_jam.Data.WebhookQueue.sql")!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync(sql); // Upgrade is safe on subsequent startups.
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_schema.Length == 0) return;
        await using var connection = new NpgsqlConnection(_baseConnection);
        await connection.OpenAsync();
        // Only the randomly generated schema owned by this test is removed.
        await using var drop = new NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
    }

    [TestMethod]
    public async Task ConcurrentRedeliveryPersistsOnlyOneJob()
    {
        var evt = Event("hello");
        await Task.WhenAll(Enqueue(evt), Enqueue(evt));
        using var scope = _services!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await db.WebhookJobs.CountAsync());
    }

    [TestMethod]
    public async Task NearbyRemindersSendOnceAcrossConcurrentAndRepeatedScans()
    {
        await SeedReminders();
        await Task.WhenAll(Reminders().ScanAndSendRemindersAsync(default), Reminders().ScanAndSendRemindersAsync(default));
        await Reminders().ScanAndSendRemindersAsync(default);
        Assert.AreEqual(1, _line.Messages.Count);
        StringAssert.Contains(_line.Messages.Single(), "2 件待辦");
        using var scope = _services!.CreateScope();
        Assert.AreEqual(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ReminderLogs.CountAsync());
    }

    [TestMethod]
    public async Task FailedBatchDoesNotMarkTasksAsSent()
    {
        await SeedReminders();
        _line.Accept = false;
        await Reminders().ScanAndSendRemindersAsync(default);
        using (var scope = _services!.CreateScope())
            Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ReminderLogs.CountAsync());
        _line.Accept = true;
        await Reminders().ScanAndSendRemindersAsync(default);
        using var finalScope = _services!.CreateScope();
        Assert.AreEqual(2, await finalScope.ServiceProvider.GetRequiredService<AppDbContext>().ReminderLogs.CountAsync());
    }

    private async Task SeedReminders()
    {
        using var scope = _services!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { LineUserId = "reminder-user" };
        var dueAt = DateTime.Now.AddHours(2);
        db.Tasks.AddRange(new TaskItem { User = user, Content = "事項一", DueAt = dueAt, Status = "pending" },
            new TaskItem { User = user, Content = "事項二", DueAt = dueAt.AddMinutes(20), Status = "pending" });
        await db.SaveChangesAsync();
    }

    private ReminderBackgroundService Reminders() => new(_services!.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<ReminderBackgroundService>.Instance, new ConfigurationBuilder().Build());

    private sealed class RecordingLineClient : ILineMessagingClient
    {
        public bool Accept { get; set; } = true;
        public List<string> Messages { get; } = new();
        public Task<bool> PushMessageAsync(string userId, string text, CancellationToken cancellationToken = default)
        {
            if (Accept) Messages.Add(text);
            return Task.FromResult(Accept);
        }
        public Task ReplyMessageAsync(string replyToken, string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplyWithLinkButtonAsync(string replyToken, string text, string buttonLabel, string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task ConfirmationAndOutboxSurviveWorkerRestartWithoutDuplicateTask()
    {
        await SeedPending();
        var evt = Event("確定");
        await Enqueue(evt);
        Assert.IsTrue(await Worker().ProcessNextAsync(default));
        await Enqueue(evt); // Redelivery after transaction commit.
        Assert.IsFalse(await Worker().ProcessNextAsync(default));
        Assert.IsTrue(await Worker().DeliverNextAsync(default));
        using var scope = _services!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await db.Tasks.CountAsync());
        var job = await db.WebhookJobs.SingleAsync();
        Assert.IsTrue(job.Processed && job.Finished);
        Assert.IsNull(job.LastError);
        StringAssert.Contains(job.ReplyMessages!, "text");
    }

    [TestMethod]
    public async Task OutboxFailureRollsBackTaskAndRetryCreatesItExactlyOnce()
    {
        await SeedPending();
        await Enqueue(Event("確定"));
        _failure.Enabled = true;
        await Worker().ProcessNextAsync(default);
        using (var scope = _services!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(0, await db.Tasks.CountAsync());
            var job = await db.WebhookJobs.SingleAsync();
            Assert.IsFalse(job.Processed);
            Assert.AreEqual(1, job.ProcessingAttempts);
            job.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        await Worker().ProcessNextAsync(default);
        using var finalScope = _services!.CreateScope();
        Assert.AreEqual(1, await finalScope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.CountAsync());
    }

    [TestMethod]
    public async Task ExpiredEventDoesNotCreateTaskAfterLongSleep()
    {
        await SeedPending();
        var evt = Event("確定");
        evt.Timestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        await Enqueue(evt);
        await Worker().ProcessNextAsync(default);
        using var scope = _services!.CreateScope();
        Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.CountAsync());
    }

    [TestMethod]
    public async Task InterruptedReplyDoesNotReplayBusinessOperationOrPush()
    {
        await SeedPending();
        await Enqueue(Event("確定"));
        await Worker().ProcessNextAsync(default);
        using (var scope = _services!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.WebhookJobs.SingleAsync();
            job.ReplyAttempted = true;
            await db.SaveChangesAsync();
        }
        await Worker().DeliverNextAsync(default);
        using var finalScope = _services!.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await finalDb.Tasks.CountAsync());
        var finalJob = await finalDb.WebhookJobs.SingleAsync();
        Assert.IsTrue(finalJob.Finished);
        Assert.IsFalse(finalJob.UsePush);
        StringAssert.Contains(finalJob.LastError!, "unknown");
    }

    private async Task SeedPending()
    {
        using var scope = _services!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            LineUserId = "user-1", PendingUpdatedAt = DateTime.Now,
            PendingTasksJson = JsonSerializer.Serialize(new[] { new PendingTask("test task", DateTime.Now.AddDays(1)) })
        });
        await db.SaveChangesAsync();
    }
    [TestMethod]
    public async Task CompletingTasksIgnoresIdsBelongingToAnotherUser()
    {
        int mine, theirs;
        using (var scope = _services!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var me = new User { LineUserId = "user-1" };
            var other = new User { LineUserId = "user-2" };
            var myTask = new TaskItem { User = me, Content = "mine", DueAt = DateTime.Now.AddDays(1), Status = "pending" };
            var otherTask = new TaskItem { User = other, Content = "theirs", DueAt = DateTime.Now.AddDays(1), Status = "pending" };
            db.Tasks.AddRange(myTask, otherTask);
            await db.SaveChangesAsync();
            (mine, theirs) = (myTask.Id, otherTask.Id);
        }

        // A hallucinated or malicious id must never complete someone else's task.
        ((FakeAi)_services!.GetRequiredService<IAiClient>()).Next = new AiResult
        {
            Success = true,
            FunctionCalls = new[]
            {
                new AiFunctionCall
                {
                    Name = "complete_tasks",
                    Args = JsonDocument.Parse($"{{\"task_ids\":[{mine},{theirs}]}}").RootElement.Clone()
                }
            }
        };

        await Enqueue(Event("都做完了"));
        await Worker().ProcessNextAsync(default);

        using var check = _services!.CreateScope();
        var final = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual("done", (await final.Tasks.SingleAsync(t => t.Id == mine)).Status);
        Assert.AreEqual("pending", (await final.Tasks.SingleAsync(t => t.Id == theirs)).Status);
    }

    private async Task Enqueue(LineEvent evt)
    {
        using var scope = _services!.CreateScope();
        await new WebhookQueue(scope.ServiceProvider.GetRequiredService<AppDbContext>()).EnqueueAsync(new[] { evt }, default);
    }
    private WebhookBackgroundService Worker() => new(_services!.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<WebhookBackgroundService>.Instance);
    private static LineEvent Event(string text) => new()
    {
        WebhookEventId = "event-1", Type = "message", ReplyToken = "token",
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Source = new LineSource { Type = "user", UserId = "user-1" },
        Message = new LineMessage { Id = "message-1", Type = "text", Text = text }
    };
    private sealed class FakeAi : IAiClient
    {
        public AiResult Next { get; set; } = new() { Success = true, Text = "test reply" };

        public Task<AiResult> GenerateAsync(string userInput, string systemInstruction, CancellationToken cancellationToken = default) =>
            Task.FromResult(Next);
    }
    private sealed class FailOutboxOnce : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<WebhookJob>().Any(e => e.Entity.Processed))
            {
                Enabled = false;
                throw new IOException("Simulated failure between task insert and outbox commit.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
