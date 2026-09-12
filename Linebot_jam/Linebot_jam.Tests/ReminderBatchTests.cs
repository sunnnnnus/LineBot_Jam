using Linebot_jam.Models;
using Linebot_jam.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Linebot_jam.Tests;

[TestClass]
public class ReminderBatchTests
{
    private static readonly DateTime Due = new(2026, 9, 14, 11, 0, 0);
    private static ReminderCandidate Item(int id, int minutes = 0, int user = 1, string stage = "3h_before") =>
        new(new TaskItem { Id = id, UserId = user, Content = $"事項 {id}", DueAt = Due.AddMinutes(minutes) }, stage);

    [TestMethod]
    public void SameAndNearbyTimesMergeButWindowDoesNotChain()
    {
        var batches = ReminderBatchBuilder.Group(new[] { Item(4, 60), Item(2, 0), Item(3, 30), Item(1, 0) }, TimeSpan.FromMinutes(30));
        Assert.AreEqual(2, batches.Count);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, batches[0].Select(c => c.Task.Id).ToArray());
        Assert.AreEqual(4, batches[1][0].Task.Id);
    }

    [TestMethod]
    public void DifferentUsersAndStagesNeverMerge()
    {
        var batches = ReminderBatchBuilder.Group(new[] { Item(1), Item(2, user: 2), Item(3, stage: "due") }, TimeSpan.FromMinutes(30));
        Assert.AreEqual(3, batches.Count);
    }

    [TestMethod]
    public void LongBatchSplitsWithoutDroppingItems()
    {
        var items = Enumerable.Range(1, 25).Select(i => Item(i)).ToArray();
        foreach (var item in items) item.Task.Content = new string('長', 200);
        var batches = ReminderBatchBuilder.Group(items, TimeSpan.FromMinutes(30));
        Assert.AreEqual(3, batches.Count);
        Assert.AreEqual(25, batches.Sum(b => b.Count));
        foreach (var batch in batches)
            Assert.IsTrue(ReminderBatchBuilder.Format(batch, Due.AddHours(-2)).Length < 4900);
    }

    [TestMethod]
    public void MessageIncludesEveryTaskAndItsOwnDeadline()
    {
        var message = ReminderBatchBuilder.Format(new[] { Item(1), Item(2, 20) }, Due.AddHours(-2));
        StringAssert.Contains(message, "🔔 你有 2 件待辦提醒");
        StringAssert.Contains(message, "1. 事項 1");
        StringAssert.Contains(message, "2. 事項 2");
        StringAssert.Contains(message, "🕒 09/14 11:00");
        StringAssert.Contains(message, "🕒 09/14 11:20");
    }
}
