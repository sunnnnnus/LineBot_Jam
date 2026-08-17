using System.Text;
using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Linebot_jam.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Controllers;

[ApiController]
[Route("api/line/webhook")]
public class LineWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILineSignatureValidator _signatureValidator;
    private readonly ILineMessagingClient _messagingClient;
    private readonly IGeminiClient _geminiClient;
    private readonly AppDbContext _db;
    private readonly ILogger<LineWebhookController> _logger;

    public LineWebhookController(
        ILineSignatureValidator signatureValidator,
        ILineMessagingClient messagingClient,
        IGeminiClient geminiClient,
        AppDbContext db,
        ILogger<LineWebhookController> logger)
    {
        _signatureValidator = signatureValidator;
        _messagingClient = messagingClient;
        _geminiClient = geminiClient;
        _db = db;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync();
        var bodyBytes = Encoding.UTF8.GetBytes(rawBody);

        var signature = Request.Headers["X-Line-Signature"].ToString();
        if (!_signatureValidator.IsValidSignature(bodyBytes, signature))
        {
            _logger.LogWarning("Rejected webhook request: invalid X-Line-Signature.");
            return Unauthorized();
        }

        LineWebhookRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LineWebhookRequest>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LINE webhook payload.");
            return BadRequest();
        }

        if (payload?.Events is null)
            return Ok();

        foreach (var evt in payload.Events)
        {
            if (evt.Type == "message" && evt.Message?.Type == "text" && !string.IsNullOrEmpty(evt.ReplyToken))
            {
                var replyText = await HandleTextMessageAsync(evt);
                await _messagingClient.ReplyMessageAsync(evt.ReplyToken, replyText);
            }
        }

        return Ok();
    }

    private async Task<string> HandleTextMessageAsync(LineEvent evt)
    {
        var text = evt.Message!.Text ?? string.Empty;
        var lineUserId = evt.Source?.UserId;

        if (TaskMessageParser.TryParse(text, DateTime.Now, out var content, out var dueAt))
        {
            if (string.IsNullOrEmpty(lineUserId))
            {
                _logger.LogWarning("Message matched task format but has no source userId; skipping DB write.");
                return "無法辨識你的使用者身分,新增失敗。";
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
            if (user is null)
            {
                user = new User { LineUserId = lineUserId };
                _db.Users.Add(user);
            }

            var task = new TaskItem
            {
                User = user,
                Content = content,
                DueAt = dueAt,
                Status = "pending",
                PriorityScore = TaskPriorityCalculator.ComputePriorityScore(dueAt)
            };
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync();

            return $"已新增:{content}\n到期時間:{dueAt:yyyy/MM/dd HH:mm}";
        }

        var geminiContext = await BuildGeminiContextAsync(lineUserId);
        var aiReply = await _geminiClient.GenerateReplyAsync(text, geminiContext);

        return aiReply
            ?? "抱歉,我現在無法理解這句話。如果要新增提醒,請用「新增 事項 日期 時間」的格式,例如:新增 倒垃圾 8/20 18:00";
    }

    private async Task<string> BuildGeminiContextAsync(string? lineUserId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一個 LINE 任務提醒機器人的助理,請用繁體中文簡短回覆使用者。");

        if (!string.IsNullOrEmpty(lineUserId))
        {
            var tasks = await _db.Tasks
                .Where(t => t.User.LineUserId == lineUserId && t.Status == "pending")
                .OrderBy(t => t.DueAt)
                .Take(10)
                .ToListAsync();

            if (tasks.Count > 0)
            {
                sb.AppendLine("使用者目前的待辦事項:");
                foreach (var t in tasks)
                    sb.AppendLine($"- {t.Content}(到期: {t.DueAt:yyyy/MM/dd HH:mm})");
            }
            else
            {
                sb.AppendLine("使用者目前沒有待辦事項。");
            }
        }

        sb.AppendLine("如果使用者想新增待辦事項,請提醒他用「新增 事項 日期 時間」的格式傳送,例如「新增 倒垃圾 8/20 18:00」。");
        return sb.ToString();
    }
}
