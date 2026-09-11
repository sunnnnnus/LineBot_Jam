namespace Linebot_jam.Models;

// Inbox and reply outbox share a row so task changes and the reply commit together.
public class WebhookJob
{
    public string EventId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public bool Processed { get; set; }
    public int ProcessingAttempts { get; set; }
    public string? ReplyMessages { get; set; }
    public string? Destination { get; set; }
    public bool ReplyAttempted { get; set; }
    public bool UsePush { get; set; }
    public Guid RetryKey { get; set; } = Guid.NewGuid();
    public DateTime? PushStartedAt { get; set; }
    public int DeliveryAttempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public bool Finished { get; set; }
    public string? LastError { get; set; }
}
