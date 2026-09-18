using System.Text.RegularExpressions;

namespace Linebot_jam.Services;

public static class WeekdayCorrection
{
    // Only a complete date-only correction is safe to apply without interpreting task content.
    private static readonly Regex Pattern = new(
        @"^(?:(?:不是[，,]?\s*)?(?:是|改成|改到|改為|應該是)\s*)?(?<week>下下|下|本|這)(?:個)?(?:禮拜|星期|週|周)(?<day>[一二三四五六日天])\s*[。.!！]?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool TryResolveDate(string text, DateTime now, out DateTime date)
    {
        date = default;
        var match = Pattern.Match(text.Trim());
        if (!match.Success) return false;
        var weeks = match.Groups["week"].Value switch { "下下" => 2, "下" => 1, _ => 0 };
        var day = "一二三四五六日".IndexOf(match.Groups["day"].Value);
        if (day < 0) day = 6; // 天 = 日
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        date = monday.AddDays(weeks * 7 + day);
        return true;
    }
}
