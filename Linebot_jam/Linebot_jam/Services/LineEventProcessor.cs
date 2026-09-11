using System.Text;
using System.Text.Json;
using Linebot_jam.Data;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services;

public class LineEventProcessor
{
    private readonly PendingLineReply _messagingClient;
    private readonly IAiClient _aiClient;
    private readonly AppDbContext _db;
    private readonly ILogger<LineEventProcessor> _logger;

    public LineEventProcessor(PendingLineReply messagingClient, IAiClient aiClient,
        AppDbContext db, ILogger<LineEventProcessor> logger)
    {
        _messagingClient = messagingClient;
        _aiClient = aiClient;
        _db = db;
        _logger = logger;
    }
    // 快速路徑:命中就不用等 AI,大部分使用者會照著我們的提示回這兩個字
    private static readonly HashSet<string> ConfirmWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "確定", "對", "好", "好的", "是", "yes", "ok", "嗯", "沒錯", "確認"
    };

    private static readonly HashSet<string> CancelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "取消", "不用", "算了", "不要", "no", "cancel"
    };

    public async Task HandleTextMessageAsync(LineEvent evt)
    {
        var text = evt.Message!.Text ?? string.Empty;
        var lineUserId = evt.Source?.UserId;
        var replyToken = evt.ReplyToken!;

        if (string.IsNullOrEmpty(lineUserId))
        {
            _logger.LogWarning("Message has no source userId; cannot process.");
            await _messagingClient.ReplyMessageAsync(replyToken, "無法辨識你的使用者身分。");
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user is null)
        {
            user = new User { LineUserId = lineUserId };
            _db.Users.Add(user);
        }

        var now = DateTime.Now;
        var pendingActive = user.PendingUpdatedAt.HasValue && now - user.PendingUpdatedAt.Value < TimeSpan.FromMinutes(10);
        if (!pendingActive) ClearPending(user);
        var awaitingConfirm = pendingActive && !string.IsNullOrEmpty(user.PendingTasksJson);

        if (awaitingConfirm)
        {
            var trimmed = text.Trim();

            if (ConfirmWords.Contains(trimmed))
            {
                await ConfirmPendingTasksAsync(user, replyToken);
                return;
            }

            if (CancelWords.Contains(trimmed))
            {
                await CancelPendingAsync(user, replyToken, "好的,已取消。");
                return;
            }

            // 不是清單裡的精確字眼,交給 AI 判斷(它會知道目前有等待確認的提議)
            await ProcessWithAiAsync(user, replyToken, text, text);
            return;
        }

        // 等待補充資訊中
        var combinedInput = pendingActive && !string.IsNullOrEmpty(user.PendingRawInput)
            ? user.PendingRawInput + "\n使用者: " + text
            : text;

        await ProcessWithAiAsync(user, replyToken, combinedInput, text);
    }

    private async Task ProcessWithAiAsync(User user, string replyToken, string combinedInput, string rawText)
    {
        var systemInstruction = await BuildSystemInstructionAsync(user);
        var result = await _aiClient.GenerateAsync(combinedInput, systemInstruction);

        if (!result.Success)
        {
            // AI 打不通時仍可使用固定格式；保留原有提議讓使用者重試。
            if (TaskMessageParser.TryParse(rawText, DateTime.Now, out var fbContent, out var fbDueAt))
            {
                SetPendingTasks(user, new List<PendingTask> { new(fbContent, fbDueAt) });
                await ConfirmPendingTasksAsync(user, replyToken);
                return;
            }

            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken,
                "AI 服務暫時忙碌，這次沒有新增任務。請稍後重試；也可以使用「新增 事項 月/日 時:分」。");
            return;
        }

        var calls = result.FunctionCalls;

        if (calls.Any(c => c.Name == "confirm_task"))
        {
            await ConfirmPendingTasksAsync(user, replyToken);
            return;
        }

        if (calls.Any(c => c.Name == "cancel_task"))
        {
            await CancelPendingAsync(user, replyToken, "好的,已取消。");
            return;
        }

        // 一則訊息可能同時列了好幾件待辦,全部都在 create_tasks 的 tasks 陣列裡
        var proposals = calls
            .Where(c => c.Name == "create_tasks")
            .SelectMany(ReadProposals)
            .ToList();

        if (proposals.Count > 0)
        {
            SetPendingTasks(user, proposals);
            user.PendingRawInput = null;
            await _db.SaveChangesAsync();

            await _messagingClient.ReplyMessageAsync(replyToken, BuildConfirmPrompt(proposals));
            return;
        }

        var clarification = calls.FirstOrDefault(c => c.Name == "ask_clarification");
        if (clarification?.Args is JsonElement clarifyArgs
            && clarifyArgs.ValueKind == JsonValueKind.Object
            && clarifyArgs.TryGetProperty("question", out var questionEl)
            && questionEl.ValueKind == JsonValueKind.String)
        {
            user.PendingTasksJson = null;
            user.PendingRawInput = combinedInput.Length > 1000 ? combinedInput[..1000] : combinedInput;
            user.PendingUpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();

            await _messagingClient.ReplyMessageAsync(replyToken, questionEl.GetString() ?? "可以再多說一點嗎?");
            return;
        }

        // AI 說要新增但參數不完整(例如時間解析不出來)
        if (calls.Any(c => c.Name == "create_tasks"))
        {
            ClearPending(user);
            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken, "抱歉,我沒有聽懂明確的時間,可以再說一次嗎?");
            return;
        }

        // 純聊天,不留任何記憶狀態
        ClearPending(user);
        await _db.SaveChangesAsync();
        await _messagingClient.ReplyMessageAsync(replyToken, result.Text ?? "嗯嗯。");
    }

    private static IEnumerable<PendingTask> ReadProposals(AiFunctionCall call)
    {
        if (call.Args is not JsonElement args
            || args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("tasks", out var tasksEl)
            || tasksEl.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in tasksEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("content", out var contentEl)
                || !item.TryGetProperty("due_at", out var dueAtEl))
            {
                continue;
            }

            if (contentEl.ValueKind != JsonValueKind.String || dueAtEl.ValueKind != JsonValueKind.String)
                continue;

            if (dueAtEl.GetString() is not string dueAtStr
                || !DateTime.TryParse(dueAtStr, out var dueAt)
                || dueAt <= DateTime.Now)
            {
                continue;
            }

            var content = contentEl.GetString() ?? string.Empty;
            if (content.Length == 0)
                continue;
            if (content.Length > 200)
                content = content[..200];

            yield return new PendingTask(content, dueAt);
        }
    }

    private static string BuildConfirmPrompt(IReadOnlyList<PendingTask> proposals)
    {
        if (proposals.Count == 1)
        {
            var only = proposals[0];
            return $"要幫你新增:「{only.Content}」\n到期時間:{only.DueAt:yyyy/MM/dd HH:mm}\n確定嗎?(回覆「確定」或「取消」)";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"要幫你新增 {proposals.Count} 筆:");
        for (var i = 0; i < proposals.Count; i++)
            sb.AppendLine($"{i + 1}. {proposals[i].Content}({proposals[i].DueAt:yyyy/MM/dd HH:mm})");
        sb.Append("確定嗎?(回覆「確定」或「取消」)");
        return sb.ToString();
    }

    private static void SetPendingTasks(User user, IReadOnlyList<PendingTask> proposals)
    {
        user.PendingTasksJson = JsonSerializer.Serialize(proposals);
        user.PendingUpdatedAt = DateTime.Now;
    }

    private static List<PendingTask> ReadPendingTasks(User user)
    {
        if (string.IsNullOrEmpty(user.PendingTasksJson))
            return new List<PendingTask>();

        try
        {
            return JsonSerializer.Deserialize<List<PendingTask>>(user.PendingTasksJson) ?? new List<PendingTask>();
        }
        catch (JsonException)
        {
            return new List<PendingTask>();
        }
    }

    private async Task ConfirmPendingTasksAsync(User user, string replyToken)
    {
        var proposals = ReadPendingTasks(user);

        if (!user.PendingUpdatedAt.HasValue || DateTime.Now - user.PendingUpdatedAt.Value >= TimeSpan.FromMinutes(10)
            || proposals.Any(p => p.DueAt <= DateTime.Now))
        {
            ClearPending(user);
            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken, "這份提議已過期，請重新提供事項與時間。");
            return;
        }

        if (proposals.Count == 0)
        {
            await _messagingClient.ReplyMessageAsync(replyToken, "目前沒有等待確認的任務喔。");
            return;
        }

        foreach (var proposal in proposals)
        {
            _db.Tasks.Add(new TaskItem
            {
                User = user,
                Content = proposal.Content,
                DueAt = proposal.DueAt,
                Status = "pending",
                PriorityScore = TaskPriorityCalculator.ComputePriorityScore(proposal.DueAt)
            });
        }

        ClearPending(user);
        await _db.SaveChangesAsync();

        var sb = new StringBuilder();
        if (proposals.Count == 1)
        {
            sb.Append($"已新增:{proposals[0].Content}\n到期時間:{proposals[0].DueAt:yyyy/MM/dd HH:mm}");
        }
        else
        {
            sb.AppendLine($"已新增 {proposals.Count} 筆:");
            for (var i = 0; i < proposals.Count; i++)
                sb.AppendLine($"{i + 1}. {proposals[i].Content}({proposals[i].DueAt:yyyy/MM/dd HH:mm})");
        }

        await _messagingClient.ReplyMessageAsync(replyToken, sb.ToString().TrimEnd());
    }

    private async Task CancelPendingAsync(User user, string replyToken, string message)
    {
        ClearPending(user);
        await _db.SaveChangesAsync();
        await _messagingClient.ReplyMessageAsync(replyToken, message);
    }

    private static void ClearPending(User user)
    {
        user.PendingTasksJson = null;
        user.PendingRawInput = null;
        user.PendingUpdatedAt = null;
    }

    private async Task<string> BuildSystemInstructionAsync(User user)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一個 LINE 任務提醒機器人的助理,請用繁體中文回覆使用者,語氣自然、簡短。");
        sb.AppendLine($"目前時間:{DateTime.Now:yyyy-MM-dd HH:mm}({DateTime.Now:dddd})");

        var tasks = await _db.Tasks
            .Where(t => t.User.LineUserId == user.LineUserId && t.Status == "pending")
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

        var pending = ReadPendingTasks(user);
        if (pending.Count > 0)
        {
            sb.AppendLine("目前有以下等待使用者確認的新待辦提議:");
            foreach (var p in pending)
                sb.AppendLine($"- {p.Content}(到期: {p.DueAt:yyyy-MM-dd HH:mm})");
            sb.AppendLine("如果使用者的回覆表示同意/確定,呼叫 confirm_task;表示不要/取消,呼叫 cancel_task;" +
                          "如果使用者提供了不同或更完整的內容與時間,呼叫 create_tasks 以新內容取代提議;如果還不清楚,才用文字回覆詢問。");
        }
        else
        {
            sb.AppendLine("如果使用者的訊息包含明確的待辦事項與到期時間,呼叫 create_tasks。");
            sb.AppendLine("**如果使用者一則訊息裡列了多件待辦事項(例如 1. 2. 3. 條列,或用頓號、換行分隔)," +
                          "請把每一件都放進 tasks 陣列一次傳回,不要只取第一件、也不要把多件合併成一筆。**");
            sb.AppendLine("如果多件待辦共用同一個時間(例如開頭只寫了一次「9/14 11點」),就把那個時間套用到每一筆。");
            sb.AppendLine("如果使用者看起來想新增待辦但缺少必要資訊(例如沒說時間),呼叫 ask_clarification 提出簡短的追問。");
        }

        sb.AppendLine("如果使用者只是聊天或詢問既有待辦事項,不要呼叫任何函式,直接文字回覆。");
        return sb.ToString();
    }
}
