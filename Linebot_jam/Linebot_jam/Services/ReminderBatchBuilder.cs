using System.Text;
using Linebot_jam.Models;

namespace Linebot_jam.Services;

public record ReminderCandidate(TaskItem Task, string Stage);

public static class ReminderBatchBuilder
{
    // Bound the total span from the first item, rather than chaining nearby items forever.
    // Ten 200-character task titles plus dates stay comfortably below LINE's text limit.
    public static IReadOnlyList<IReadOnlyList<ReminderCandidate>> Group(
        IEnumerable<ReminderCandidate> candidates, TimeSpan window)
    {
        var batches = new List<IReadOnlyList<ReminderCandidate>>();
        foreach (var group in candidates.GroupBy(c => (c.Task.UserId, c.Stage)))
        {
            var current = new List<ReminderCandidate>();
            foreach (var candidate in group.OrderBy(c => c.Task.DueAt).ThenBy(c => c.Task.Id))
            {
                if (current.Count > 0 && (current.Count == 10 ||
                    candidate.Task.DueAt - current[0].Task.DueAt > window))
                {
                    batches.Add(current);
                    current = new List<ReminderCandidate>();
                }
                current.Add(candidate);
            }
            if (current.Count > 0) batches.Add(current);
        }
        return batches;
    }

    public static string Format(IReadOnlyList<ReminderCandidate> batch, DateTime now)
    {
        var text = new StringBuilder(batch.Count == 1 ? "🔔 待辦提醒" : $"🔔 你有 {batch.Count} 件待辦提醒");
        for (var i = 0; i < batch.Count; i++)
        {
            var task = batch[i].Task;
            text.Append($"\n\n{i + 1}. {task.Content}\n🕒 {task.DueAt:MM/dd HH:mm}（{ReminderStageSelector.Describe(task.DueAt - now)}）");
        }
        return text.ToString();
    }
}
