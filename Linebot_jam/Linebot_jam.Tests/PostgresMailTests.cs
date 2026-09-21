using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Linebot_jam.Options;
using Linebot_jam.Services.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Linebot_jam.Tests;

public partial class PostgresRecoveryTests
{
    private sealed class FakeMailbox : IGmailReader
    {
        public Task<string> GetAccountAsync(CancellationToken ct) => Task.FromResult("me@example.com");
        public Task<MailPage> ListAsync(DateTimeOffset since, string? pageToken, CancellationToken ct) =>
            Task.FromResult(new MailPage(new[] { "email-1" }, null));
        public Task<SchoolMail?> ReadAsync(string id, CancellationToken ct) => Task.FromResult<SchoolMail?>(new SchoolMail(id, "作業與公告", "信件完整內容", DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
    private sealed class FakeClassifier : IMailClassifier
    {
        public int Calls;
        public bool Fail;
        public IReadOnlyList<MailDecision> Decisions = new[]
        {
            new MailDecision { Kind = "task", Content = "英文作業", DueAt = MailTime.Local(DateTimeOffset.UtcNow.AddDays(2)).ToString("yyyy-MM-dd'T'HH:mm:ss") },
            new MailDecision { Kind = "notice", Content = "明天停課" }
        };
        public Task<IReadOnlyList<MailDecision>> ClassifyAsync(SchoolMail mail, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("AI temporarily down");
            return Task.FromResult(Decisions);
        }
    }
    private async Task SyncMail(FakeClassifier ai, bool backfill = true, string owner = "user-1")
    {
        using var scope = _services!.CreateScope();
        var options = Microsoft.Extensions.Options.Options.Create(new GmailOptions
        {
            Enabled = true, AccountEmail = "me@example.com", OwnerLineUserId = owner,
            ClientId = "test", ClientSecret = "test", RefreshToken = "test",
            StartAtUtc = backfill ? DateTimeOffset.UtcNow.AddDays(-2) : null
        });
        await new MailSyncService(scope.ServiceProvider.GetRequiredService<AppDbContext>(), new FakeMailbox(), ai,
            options, TimeProvider.System, NullLogger<MailSyncService>.Instance).SyncAsync(default);
    }
    private static LineEvent MailClick(Guid id, string userId = "user-1", int version = 1) => new()
    {
        WebhookEventId = Guid.NewGuid().ToString(), Type = "postback", ReplyToken = "token",
        Source = new LineSource { Type = "user", UserId = userId }, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Postback = new LinePostback { Data = $"mail:confirm:{id:N}:{version}" }
    };

    [TestMethod]
    public async Task MailSyncDeduplicatesAndConfirmationIsOwnedAndIdempotent()
    {
        await SeedPending();
        var ai = new FakeClassifier();
        await Task.WhenAll(SyncMail(ai), SyncMail(ai));
        await SyncMail(ai);
        Guid proposalId;
        using (var scope = _services!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(1, await db.MailReceipts.CountAsync());
            Assert.AreEqual(0, await db.Tasks.CountAsync());
            var proposal = await db.MailProposals.SingleAsync();
            proposalId = proposal.Id;
            var push = await db.WebhookJobs.SingleAsync();
            Assert.AreEqual("user-1", push.Destination);
            Assert.AreEqual(2, JsonDocument.Parse(push.ReplyMessages!).RootElement.GetArrayLength());
            Assert.IsNotNull((await db.Users.SingleAsync()).PendingTasksJson); // Ordinary chat proposal survives.
        }
        Assert.AreEqual(1, ai.Calls);
        await Enqueue(MailClick(proposalId, "someone-else"));
        await Worker().ProcessNextAsync(default);
        using (var scope = _services!.CreateScope()) Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.CountAsync());
        await Enqueue(MailClick(proposalId));
        await Worker().ProcessNextAsync(default);
        await Enqueue(MailClick(proposalId));
        await Worker().ProcessNextAsync(default);
        using var final = _services!.CreateScope();
        var finalDb = final.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await finalDb.Tasks.CountAsync());
        Assert.AreEqual("accepted", (await finalDb.MailProposals.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task MissingDeadlineNeedsEditAndOldButtonsCannotConfirmNewDate()
    {
        await SeedPending();
        var ai = new FakeClassifier { Decisions = new[] { new MailDecision { Kind = "task", Content = "英文作業" } } };
        await SyncMail(ai);
        Guid id;
        using (var scope = _services!.CreateScope()) id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().MailProposals.SingleAsync()).Id;
        await Enqueue(MailClick(id));
        await Worker().ProcessNextAsync(default);
        var edit = MailClick(id);
        edit.Postback = new LinePostback { Data = $"mail:edit:{id:N}:1", Params = new LinePostbackParams
        { Datetime = MailTime.Local(DateTimeOffset.UtcNow.AddDays(1)).ToString("yyyy-MM-dd'T'HH:mm") } };
        edit.WebhookEventId = "mail-edit";
        await Enqueue(edit);
        await Worker().ProcessNextAsync(default);
        await Enqueue(MailClick(id)); // Old button from version 1 must not accept version 2.
        await Worker().ProcessNextAsync(default);
        using (var scope = _services!.CreateScope()) Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.CountAsync());
        await Enqueue(MailClick(id, version: 2));
        await Worker().ProcessNextAsync(default);
        using var final = _services!.CreateScope();
        Assert.AreEqual(1, await final.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.CountAsync());
    }

    [TestMethod]
    public async Task FailedClassificationRetriesWithoutLosingMessage()
    {
        await SeedPending();
        var ai = new FakeClassifier { Fail = true };
        await SyncMail(ai);
        using (var scope = _services!.CreateScope()) Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().MailReceipts.CountAsync());
        ai.Fail = false;
        await SyncMail(ai);
        Assert.AreEqual(2, ai.Calls);
    }

    [TestMethod]
    public async Task DefaultStartBoundarySkipsExistingMail()
    {
        await SeedPending();
        var ai = new FakeClassifier();
        await SyncMail(ai, backfill: false);
        Assert.AreEqual(0, ai.Calls);
    }

    [TestMethod]
    public async Task MailboxCannotBeSilentlyReboundToAnotherLineUser()
    {
        await SeedPending();
        var ai = new FakeClassifier();
        await SyncMail(ai);
        using (var scope = _services!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { LineUserId = "user-2" });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => SyncMail(ai, owner: "user-2"));
        Assert.AreEqual(1, ai.Calls);
    }

    [TestMethod]
    public async Task ExpiredTaskAndOldNoticeDoNotCreateReminders()
    {
        await SeedPending();
        await SyncMail(new FakeClassifier { Decisions = new[]
        {
            new MailDecision { Kind = "task", Content = "過期作業", DueAt = "2020-01-01T09:00:00" },
            new MailDecision { Kind = "notice", Content = "過期停課", EventDate = "2020-01-01" }
        }});
        using var scope = _services!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(0, await db.MailProposals.CountAsync());
        Assert.AreEqual(0, await db.Tasks.CountAsync());
        Assert.AreEqual(1, JsonDocument.Parse((await db.WebhookJobs.SingleAsync()).ReplyMessages!).RootElement.GetArrayLength());
    }
}
