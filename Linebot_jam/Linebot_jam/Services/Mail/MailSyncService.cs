using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models;
using Linebot_jam.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Linebot_jam.Services.Mail;

public sealed class MailSyncService(AppDbContext db, IGmailReader gmail, IMailClassifier classifier,
    IOptions<GmailOptions> options, TimeProvider clock, ILogger<MailSyncService> logger)
{
    public async Task SyncAsync(CancellationToken ct)
    {
        var config = options.Value;
        if (!config.Enabled) return;
        if (!config.IsConfigured) throw new InvalidOperationException("Gmail settings incomplete; see docs/gmail.md.");
        var owner = await db.Users.SingleOrDefaultAsync(u => u.LineUserId == config.OwnerLineUserId, ct)
            ?? throw new InvalidOperationException("Gmail owner must be an existing, explicitly configured LINE user.");
        var account = (await gmail.GetAccountAsync(ct)).ToLowerInvariant();
        if (!account.Equals(config.AccountEmail, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Authorized Gmail account does not match configured account.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(74129005) AS \"Value\"").SingleAsync(ct)) return;
        var now = clock.GetUtcNow();
        var state = await db.MailSyncStates.SingleOrDefaultAsync(s => s.Account == account, ct);
        if (state is null)
        {
            var start = config.StartAtUtc?.ToUniversalTime() ?? now;
            if (start > now) throw new InvalidOperationException("Gmail start boundary cannot be in the future.");
            state = new MailSyncState { Account = account, UserId = owner.Id, StartedAt = start.UtcDateTime, LastSyncedAt = start.UtcDateTime };
            db.MailSyncStates.Add(state);
        }
        if (state.UserId != owner.Id) throw new InvalidOperationException("Gmail is already bound to another LINE user; refusing reassignment.");
        var since = state.LastSyncedAt.AddDays(-2); // Re-read overlap; receipt IDs make retries harmless.
        if (since < state.StartedAt) since = state.StartedAt;
        string? pageToken = null;
        var complete = true;
        do
        {
            var page = await gmail.ListAsync(new DateTimeOffset(since), pageToken, ct);
            foreach (var id in page.Ids)
            {
                var key = account + ":" + id;
                if (await db.MailReceipts.AnyAsync(r => r.Key == key, ct)) continue;
                SchoolMail? mail;
                IReadOnlyList<MailDecision> decisions;
                try
                {
                    mail = await gmail.ReadAsync(id, ct);
                    if (mail is null) continue;
                    if (mail.ReceivedAt.UtcDateTime < state.StartedAt || mail.ReceivedAt > now) continue;
                    decisions = await classifier.ClassifyAsync(mail, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Keep the cursor so this message can retry, but commit other successful imports.
                    logger.LogWarning("Mail read/classification failed ({ErrorType}); retry on next poll.", ex.GetType().Name);
                    complete = false;
                    continue;
                }
                db.MailReceipts.Add(new MailReceipt { Key = key, ReceivedAt = mail.ReceivedAt.UtcDateTime });
                var messages = new List<object>();
                foreach (var decision in decisions)
                {
                    if (decision.Kind == "notice")
                    {
                        if (decision.EventDate is not null && DateTime.ParseExact(decision.EventDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).Date < MailTime.Local(now).Date) continue;
                        messages.Add(MailMessages.Text($"📢 {mail.Subject}\n{decision.Content}\n（TronClass 郵件；僅通知，未新增提醒）"));
                        continue;
                    }
                    var due = MailTime.ParseDue(decision.DueAt);
                    if (due.HasValue && due <= MailTime.Local(now))
                    {
                        messages.Add(MailMessages.Text($"📬 {decision.Content}\n信件截止時間 {due:yyyy/MM/dd HH:mm} 已過，未新增提醒。"));
                        continue;
                    }
                    var proposal = new MailProposal { ReceiptKey = key, UserId = owner.Id, Content = decision.Content, DueAt = due };
                    db.MailProposals.Add(proposal);
                    messages.Add(MailMessages.Proposal(proposal, mail.Subject));
                }
                // LINE accepts at most five messages per push; each outbox has a stable retry key.
                foreach (var chunk in messages.Chunk(5))
                    db.WebhookJobs.Add(new WebhookJob
                    {
                        EventId = "mail-push:" + Guid.NewGuid().ToString("N"), Payload = "{}", Processed = true,
                        UsePush = true, Destination = owner.LineUserId, ReceivedAt = now.UtcDateTime,
                        NextAttemptAt = now.UtcDateTime, ReplyMessages = JsonSerializer.Serialize(chunk)
                    });
                await db.SaveChangesAsync(ct); // Receipt + proposals + outbox committed together below.
            }
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
        if (complete) state.LastSyncedAt = now.UtcDateTime;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

public sealed class MailBackgroundService(IServiceScopeFactory scopes, IOptions<GmailOptions> options,
    ILogger<MailBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MailSyncService>().SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Gmail sync failed ({ErrorType}); check Gmail settings, authorization and database readiness. Will retry.", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(options.Value.PollMinutes, 1, 60)), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
