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
    private readonly LineUserContext _currentUser;

    public LineEventProcessor(PendingLineReply messagingClient, IAiClient aiClient,
        AppDbContext db, ILogger<LineEventProcessor> logger, LineUserContext currentUser)
    {
        _messagingClient = messagingClient;
        _aiClient = aiClient;
        _db = db;
        _logger = logger;
        _currentUser = currentUser;
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
        var replyToken = evt.ReplyToken!;

        var user = await _currentUser.ResolveAsync(evt.Source);
        if (user is null)
        {
            _logger.LogWarning("Message has no source userId; cannot process.");
            await _messagingClient.ReplyMessageAsync(replyToken, "無法辨識你的使用者身分。");
            return;
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
                await CancelPendingAsync(user, replyToken, "👌 好的,已取消。");
                return;
            }

            if (WeekdayCorrection.TryResolveDate(trimmed, now, out var correctedDate))
            {
                var pending = ReadPendingTasks(user);
                if (pending.Count != 1)
                {
                    await _messagingClient.ReplyMessageAsync(replyToken, "📝 請指定要修改哪一件待辦，以及完整日期與時間；目前還沒有新增。");
                    return;
                }
                var corrected = new PendingTask(pending[0].Content, correctedDate + pending[0].DueAt.TimeOfDay);
                if (corrected.DueAt <= now)
                {
                    await _messagingClient.ReplyMessageAsync(replyToken, "📝 這個時間已經過了，請提供未來的日期與時間；目前還沒有新增。");
                    return;
                }
                SetPendingTasks(user, new[] { corrected });
                await _db.SaveChangesAsync();
                await _messagingClient.ReplyMessageAsync(replyToken, BuildConfirmPrompt(new[] { corrected }));
                return;
            }

            // 其他補充交給 AI 產生新提議，AI 不可代替使用者確認寫入。
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
                await _db.SaveChangesAsync();
                await _messagingClient.ReplyMessageAsync(replyToken, BuildConfirmPrompt(ReadPendingTasks(user)));
                return;
            }

            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken,
                "⏳ AI 服務暫時忙碌，這次沒有新增任務。請稍後重試；也可以使用「新增 事項 月/日 時:分」。");
            return;
        }

        var calls = result.FunctionCalls;

        if (calls.Any(c => c.Name == "cancel_task"))
        {
            await CancelPendingAsync(user, replyToken, "👌 好的,已取消。");
            return;
        }

        // 一則訊息可能同時列了好幾件待辦,全部都在 create_tasks 的 tasks 陣列裡
        var proposals = calls
            .Where(c => c.Name == "create_tasks")
            .SelectMany(ReadProposals)
            .ToList();

        // 「倒垃圾做完了,另外明天要開會」這種訊息會同時有完成與新增,合併成一則回覆。
        var completion = calls.FirstOrDefault(c => c.Name == "complete_tasks");
        if (completion is not null)
        {
            var summary = await CompleteTasksAsync(user, completion);
            if (proposals.Count > 0)
            {
                SetPendingTasks(user, proposals);
                user.PendingRawInput = null;
                summary += "\n\n" + BuildConfirmPrompt(proposals);
            }

            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken, summary);
            return;
        }

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

        // 保留待確認內容；模型誤回 confirm_task 或純文字都不能新增或悄悄清除提議。
        var pendingTasks = ReadPendingTasks(user);
        if (pendingTasks.Count > 0)
        {
            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken,
                "📝 尚未新增。若要修改，請提供完整事項、日期與時間。\n\n" + BuildConfirmPrompt(pendingTasks));
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
            return $"📝 要幫你新增:「{only.Content}」\n到期時間:{only.DueAt:yyyy/MM/dd HH:mm}\n確定嗎?(回覆「確定」或「取消」)";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"📝 要幫你新增 {proposals.Count} 筆:");
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
        var stored = ReadPendingTasks(user);

        if (!user.PendingUpdatedAt.HasValue || DateTime.Now - user.PendingUpdatedAt.Value >= TimeSpan.FromMinutes(10))
        {
            ClearPending(user);
            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken, "這份提議已過期，請重新提供事項與時間。");
            return;
        }

        if (stored.Count == 0)
        {
            await _messagingClient.ReplyMessageAsync(replyToken, "目前沒有等待確認的任務喔。");
            return;
        }

        // 只丟掉真的已過期的那幾筆，其餘照常新增；整批作廢會讓使用者白打一串清單。
        var proposals = stored.Where(p => p.DueAt > DateTime.Now).ToList();
        var skipped = stored.Count - proposals.Count;

        if (proposals.Count == 0)
        {
            ClearPending(user);
            await _db.SaveChangesAsync();
            await _messagingClient.ReplyMessageAsync(replyToken, "這份提議已過期，請重新提供事項與時間。");
            return;
        }

        foreach (var proposal in proposals)
        {
            _currentUser.AddTask(new TaskItem
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
            sb.Append($"✅ 已新增:{proposals[0].Content}\n到期時間:{proposals[0].DueAt:yyyy/MM/dd HH:mm}");
        }
        else
        {
            sb.AppendLine($"✅ 已新增 {proposals.Count} 筆:");
            for (var i = 0; i < proposals.Count; i++)
                sb.AppendLine($"{i + 1}. {proposals[i].Content}({proposals[i].DueAt:yyyy/MM/dd HH:mm})");
        }

        if (skipped > 0)
            sb.Append($"\n\n⚠️ 另有 {skipped} 筆的時間已經過了,沒有新增,需要的話再告訴我新的時間。");

        await _messagingClient.ReplyMessageAsync(replyToken, sb.ToString().TrimEnd());
    }

    // 回傳要給使用者看的結果文字;不在這裡 SaveChanges,交給呼叫端跟其他變更一起提交。
    private async Task<string> CompleteTasksAsync(User user, AiFunctionCall call)
    {
        var ids = ReadTaskIds(call);
        if (ids.Count == 0)
            return "我不確定你指的是哪一筆待辦,可以說得更具體一點嗎?";

        // 使用者主鍵由已驗證的 webhook 決定，不能採用 AI 提供的身分或任務擁有者。
        var tasks = await _currentUser.Tasks
            .Where(t => t.Status == "pending" && ids.Contains(t.Id))
            .ToListAsync();

        if (tasks.Count == 0)
            return "這幾筆待辦我找不到(可能已經完成過了),你可以先問我目前還有哪些待辦。";

        foreach (var task in tasks)
        {
            task.Status = "done";
            task.UpdatedAt = DateTime.Now;
        }

        var sb = new StringBuilder();
        if (tasks.Count == 1)
        {
            sb.Append($"✅ 已完成:{tasks[0].Content}");
        }
        else
        {
            sb.AppendLine($"✅ 已完成 {tasks.Count} 筆:");
            foreach (var task in tasks)
                sb.AppendLine($"・{task.Content}");
        }

        var missing = ids.Count - tasks.Count;
        if (missing > 0)
            sb.Append($"\n\n⚠️ 另有 {missing} 筆找不到或已經完成過了。");

        return sb.ToString().TrimEnd();
    }

    private static List<int> ReadTaskIds(AiFunctionCall call)
    {
        var ids = new List<int>();
        if (call.Args is not JsonElement args
            || args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("task_ids", out var idsEl)
            || idsEl.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var item in idsEl.EnumerateArray())
        {
            // 模型偶爾會把數字包成字串,兩種都收。
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                ids.Add(number);
            else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed))
                ids.Add(parsed);
        }

        return ids.Distinct().ToList();
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
        sb.AppendLine("回覆可自然加入 1～2 個合適的 emoji（例如 😊、📝、✅），避免每句都加；嚴肅或災害相關問題保持克制。工具參數中的待辦內容請保留原意，不要自行加 emoji。");
        sb.AppendLine($"目前時間:{DateTime.Now:yyyy-MM-dd HH:mm}({DateTime.Now:dddd})");
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        sb.AppendLine($"週一為一週開始。本週是 {monday:yyyy/MM/dd} 至 {monday.AddDays(6):yyyy/MM/dd}；下週是 {monday.AddDays(7):yyyy/MM/dd} 至 {monday.AddDays(13):yyyy/MM/dd}。下禮拜日/下週日是 {monday.AddDays(13):yyyy/MM/dd}，不是本週日。");

        var tasks = await _currentUser.Tasks
            .Where(t => t.Status == "pending")
            .OrderBy(t => t.DueAt)
            .Take(30)
            .ToListAsync();

        if (tasks.Count > 0)
        {
            // 帶編號讓使用者可以用自然語言指涉某一筆(complete_tasks 要用這個編號)
            sb.AppendLine("使用者目前的待辦事項(編號供 complete_tasks 使用):");
            foreach (var t in tasks)
                sb.AppendLine($"- [{t.Id}] {t.Content}(到期: {t.DueAt:yyyy/MM/dd HH:mm})");
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
            sb.AppendLine("使用者提供不同或更完整的內容與時間時，呼叫 create_tasks 以新內容取代提議；只改日期時保留原本事項與時分。" +
                          "例如「下禮拜日」是修正日期，不是同意新增。每次修改都必須重新確認。不要宣稱已新增，程式會在使用者明確回覆「確定」後儲存。表示取消時呼叫 cancel_task；不清楚時呼叫 ask_clarification。");
        }
        else
        {
            sb.AppendLine("如果使用者的訊息包含明確的待辦事項與到期時間,呼叫 create_tasks。");
            sb.AppendLine("**如果使用者一則訊息裡列了多件待辦事項(例如 1. 2. 3. 條列,或用頓號、換行分隔)," +
                          "請把每一件都放進 tasks 陣列一次傳回,不要只取第一件、也不要把多件合併成一筆。**");
            sb.AppendLine("如果多件待辦共用同一個時間(例如開頭只寫了一次「9/14 11點」),就把那個時間套用到每一筆。");
            sb.AppendLine("如果使用者看起來想新增待辦但缺少必要資訊(例如沒說時間),呼叫 ask_clarification 提出簡短的追問。");
        }

        if (tasks.Count > 0)
            sb.AppendLine("如果使用者表示某些既有待辦已經做完,呼叫 complete_tasks 並填入對應的編號。");

        sb.AppendLine("如果使用者只是聊天或詢問既有待辦事項,不要呼叫任何函式,直接文字回覆。");
        return sb.ToString();
    }
}
