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

    public static string Describe(TimeSpan remaining) => remaining <= TimeSpan.Zero
        ? "已到期"
        : $"約剩 {Math.Ceiling(remaining.TotalMinutes)} 分鐘到期";
}
