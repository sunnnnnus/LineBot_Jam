namespace Linebot_jam.Models;

// Reserves a task/stage before HTTP delivery; the durable job owns the frozen message.
public class ReminderDispatch
{
    public int TaskId { get; set; }
    public string ReminderType { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
}
