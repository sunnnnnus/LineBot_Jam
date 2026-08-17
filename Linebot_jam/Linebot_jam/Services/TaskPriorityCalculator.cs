namespace Linebot_jam.Services;

public static class TaskPriorityCalculator
{
    // Fixed reference point — PriorityScore is a pure function of DueAt only,
    // never of CreatedAt or "now", so it stays correctly orderable no matter when it's computed.
    private static readonly DateTime Epoch = new(2020, 1, 1);

    public static int ComputePriorityScore(DateTime dueAt)
    {
        var hoursSinceEpoch = (dueAt - Epoch).TotalHours;
        return -(int)Math.Round(hoursSinceEpoch);
    }
}
