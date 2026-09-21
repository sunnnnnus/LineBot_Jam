using System.Globalization;
using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services.Mail;

public sealed class MailInteraction(AppDbContext db, PendingLineReply reply, TimeProvider clock)
{
    public async Task HandleAsync(LineEvent evt)
    {
        if (evt.Source?.Type != "user" || string.IsNullOrWhiteSpace(evt.Source.UserId))
        {
            await reply.ReplyMessageAsync(evt.ReplyToken!, "請在與機器人的一對一聊天室處理郵件提醒。");
            return;
        }
        var text = evt.Message?.Text?.Trim() ?? "";
        Guid id;
        string action;
        int version = 0;
        DateTime? newDue = null;
        if (evt.Postback?.Data is string data)
        {
            var parts = data.Split(':');
            if (parts.Length != 4 || parts[0] != "mail" || !Guid.TryParseExact(parts[2], "N", out id)
                || !int.TryParse(parts[3], out version) || parts[1] is not ("confirm" or "dismiss" or "edit")) return;
            action = parts[1];
            if (action == "edit")
            {
                if (!DateTime.TryParseExact(evt.Postback.Params?.Datetime, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked))
                {
                    await reply.ReplyMessageAsync(evt.ReplyToken!, "無法辨識所選時間，請重新選擇。");
                    return;
                }
                newDue = picked;
            }
        }
        else
        {
            var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out id) ||
                !DateTime.TryParseExact(parts[2], "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                await reply.ReplyMessageAsync(evt.ReplyToken!, "請使用：郵件時間 郵件編號 yyyy/MM/dd HH:mm（台灣時間）");
                return;
            }
            action = "edit";
            newDue = parsed;
        }
        // Worker serializes all webhook mutations and commits task + reply atomically.
        var proposal = await db.MailProposals.SingleOrDefaultAsync(p => p.Id == id &&
            db.Users.Any(u => u.Id == p.UserId && u.LineUserId == evt.Source.UserId));
        if (proposal is null)
        {
            await reply.ReplyMessageAsync(evt.ReplyToken!, "找不到這份郵件提議。");
            return;
        }
        if (proposal.Status != "pending")
        {
            await reply.ReplyMessageAsync(evt.ReplyToken!, "這份郵件提議已經處理過了，不會重複新增。");
            return;
        }
        if (evt.Postback is not null && version != proposal.Version)
        {
            reply.SetMessages(new[] { MailMessages.Text("日期已修改，請使用下方最新提議確認。"), MailMessages.Proposal(proposal) });
            return;
        }
        if (action == "dismiss")
        {
            proposal.Status = "dismissed";
            await reply.ReplyMessageAsync(evt.ReplyToken!, "👌 已略過這項郵件待辦。");
        }
        else if (action == "edit")
        {
            if (newDue <= MailTime.Local(clock.GetUtcNow()))
            {
                await reply.ReplyMessageAsync(evt.ReplyToken!, "這個時間已經過了，請提供未來的截止時間。");
                return;
            }
            proposal.DueAt = newDue;
            proposal.Version++;
            reply.SetMessages(new[] { MailMessages.Proposal(proposal) });
        }
        else if (!proposal.DueAt.HasValue || proposal.DueAt <= MailTime.Local(clock.GetUtcNow()))
        {
            reply.SetMessages(new[] { MailMessages.Text("截止時間缺漏或已過期，請先補充或修改時間，尚未新增。"), MailMessages.Proposal(proposal) });
        }
        else
        {
            var task = new TaskItem { UserId = proposal.UserId, Content = proposal.Content, DueAt = proposal.DueAt.Value,
                Status = "pending", PriorityScore = TaskPriorityCalculator.ComputePriorityScore(proposal.DueAt.Value) };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            proposal.TaskId = task.Id;
            proposal.Status = "accepted";
            await reply.ReplyMessageAsync(evt.ReplyToken!, $"✅ 已新增：{task.Content}\n截止：{task.DueAt:yyyy/MM/dd HH:mm}\n會套用原有提醒規則，不補發已錯過的提前提醒。");
        }
        await db.SaveChangesAsync();
    }
}
