using Linebot_jam.Models;

namespace Linebot_jam.Services.Mail;

public static class MailMessages
{
    public static object Text(string text)
    {
        if (text.Length > 4900)
        {
            var length = char.IsHighSurrogate(text[4899]) ? 4899 : 4900;
            text = text[..length] + "\n（請查看原信完整內容）";
        }
        return new { type = "text", text };
    }
    public static object Proposal(MailProposal p, string subject = "校務郵件")
    {
        var actions = new List<object>();
        if (p.DueAt.HasValue)
            actions.Add(new { type = "button", action = new { type = "postback", label = "新增提醒", data = $"mail:confirm:{p.Id:N}:{p.Version}" } });
        actions.Add(new { type = "button", action = new { type = "datetimepicker", label = "修改時間", data = $"mail:edit:{p.Id:N}:{p.Version}", mode = "datetime" } });
        actions.Add(new { type = "button", action = new { type = "postback", label = "略過", data = $"mail:dismiss:{p.Id:N}:{p.Version}" } });
        var deadline = p.DueAt.HasValue ? $"截止：{p.DueAt:yyyy/MM/dd HH:mm}（台灣時間）" : "信件沒有明確截止日期與時分，請補充後再確認。";
        return new
        {
            type = "flex",
            altText = "📝 郵件待辦：" + p.Content,
            contents = new
            {
                type = "bubble",
                body = new { type = "box", layout = "vertical", contents = new[]
                {
                    new { type = "text", text = $"📝 {(subject.Length > 150 ? subject[..150] : subject)}\n{p.Content}\n{deadline}\n要新增提醒嗎？", wrap = true }
                } },
                footer = new { type = "box", layout = "vertical", contents = actions }
            }
        };
    }
}
