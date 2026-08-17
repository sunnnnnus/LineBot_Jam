using System;
using System.Collections.Generic;

namespace Linebot_jam.Models;

public partial class TaskItem
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string Content { get; set; } = null!;

    public DateTime DueAt { get; set; }

    public int? PriorityScore { get; set; }

    public int? ComplexityScore { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<ReminderLog> ReminderLogs { get; set; } = new List<ReminderLog>();

    public virtual User User { get; set; } = null!;
}
