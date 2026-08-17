using System.Text.RegularExpressions;

namespace Linebot_jam.Services;

public static class TaskMessageParser
{
    private static readonly Regex Pattern = new(
        @"^新增\s+(?<content>.+?)\s+(?<month>\d{1,2})/(?<day>\d{1,2})\s+(?<hour>\d{1,2}):(?<minute>\d{2})\s*$",
        RegexOptions.Compiled);

    public static bool TryParse(string text, DateTime now, out string content, out DateTime dueAt)
    {
        content = string.Empty;
        dueAt = default;

        var match = Pattern.Match(text.Trim());
        if (!match.Success)
            return false;

        var month = int.Parse(match.Groups["month"].Value);
        var day = int.Parse(match.Groups["day"].Value);
        var hour = int.Parse(match.Groups["hour"].Value);
        var minute = int.Parse(match.Groups["minute"].Value);

        DateTime candidate;
        try
        {
            candidate = new DateTime(now.Year, month, day, hour, minute, 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (candidate < now)
            candidate = candidate.AddYears(1);

        content = match.Groups["content"].Value.Trim();
        if (content.Length == 0)
            return false;

        dueAt = candidate;
        return true;
    }
}
