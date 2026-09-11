namespace Linebot_jam.Services;

public static class ReminderStageSelector
{
    // Select just the currently relevant stage after startup; never replay obsolete stages.
    public static string? Select(TimeSpan remaining) => remaining switch
    {
        var value when value <= TimeSpan.Zero => "due",
        var value when value <= TimeSpan.FromHours(3) => "3h_before",
        var value when value <= TimeSpan.FromDays(1) => "1d_before",
        var value when value <= TimeSpan.FromDays(3) => "3d_before",
        _ => null
    };

    public static string Describe(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "已到期";
        if (remaining.TotalDays >= 1)
        {
            var hours = (int)Math.Ceiling(remaining.TotalHours);
            var days = hours / 24;
            var extraHours = hours % 24;
            return extraHours == 0 ? $"約剩 {days} 天到期" : $"約剩 {days} 天 {extraHours} 小時到期";
        }
        if (remaining.TotalHours >= 1)
        {
            var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            var hours = minutes / 60;
            var extraMinutes = minutes % 60;
            return extraMinutes == 0 ? $"約剩 {hours} 小時到期" : $"約剩 {hours} 小時 {extraMinutes} 分鐘到期";
        }
        return $"約剩 {Math.Ceiling(remaining.TotalMinutes)} 分鐘到期";
    }
}
