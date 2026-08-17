using System;
using System.Collections.Generic;

namespace Linebot_jam.Models;

public partial class User
{
    public int Id { get; set; }

    public string LineUserId { get; set; } = null!;

    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
}
