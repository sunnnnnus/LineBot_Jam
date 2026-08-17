using System;
using System.Collections.Generic;

namespace Linebot_jam.Models;

public partial class ReminderLog
{
    public int Id { get; set; }

    public int TaskId { get; set; }

    public string ReminderType { get; set; } = null!;

    public DateTime SentAt { get; set; }

    public string? Channel { get; set; }

    public virtual TaskItem Task { get; set; } = null!;
}
