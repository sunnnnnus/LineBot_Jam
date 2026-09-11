using System;
using System.Collections.Generic;

namespace Linebot_jam.Models;

public partial class User
{
    public int Id { get; set; }

    public string LineUserId { get; set; } = null!;

    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? PendingTasksJson { get; set; }

    public string? PendingRawInput { get; set; }

    public DateTime? PendingUpdatedAt { get; set; }

    public virtual ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
}
