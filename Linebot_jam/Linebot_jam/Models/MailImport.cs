namespace Linebot_jam.Models;

public sealed class MailSyncState
{
    public string Account { get; set; } = "";
    public int UserId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime LastSyncedAt { get; set; }
}

public sealed class MailReceipt
{
    public string Key { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}

public sealed class MailProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ReceiptKey { get; set; } = "";
    public int UserId { get; set; }
    public string Content { get; set; } = "";
    public DateTime? DueAt { get; set; }
    public string Status { get; set; } = "pending";
    public int Version { get; set; } = 1;
    public int? TaskId { get; set; }
}
