using Linebot_jam.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Linebot_jam.Tests;

[TestClass]
public class WeekdayCorrectionTests
{
    [DataTestMethod]
    [DataRow("下禮拜日", "2026-09-13", "2026-09-20")]
    [DataRow("下週日", "2026-09-13", "2026-09-20")]
    [DataRow("改成下星期天！", "2026-09-13", "2026-09-20")]
    [DataRow("下週一", "2026-09-13", "2026-09-14")]
    [DataRow("下下週日", "2026-09-13", "2026-09-27")]
    [DataRow("本週日", "2026-09-13", "2026-09-13")]
    [DataRow("下週日", "2026-09-18", "2026-09-27")]
    [DataRow("下週一", "2026-12-31", "2027-01-04")]
    public void ResolvesCalendarWeek(string input, string today, string expected)
    {
        Assert.IsTrue(WeekdayCorrection.TryResolveDate(input, DateTime.Parse(today), out var actual));
        Assert.AreEqual(DateTime.Parse(expected), actual);
    }

    [DataTestMethod]
    [DataRow("下禮拜日早上十點")]
    [DataRow("不要改成下週日")]
    [DataRow("下週日考英文，下週一開會")]
    [DataRow("確定")]
    public void DoesNotGuessForCompoundOrNegativeMessages(string input)
    {
        Assert.IsFalse(WeekdayCorrection.TryResolveDate(input, new DateTime(2026, 9, 13), out _));
    }
}
