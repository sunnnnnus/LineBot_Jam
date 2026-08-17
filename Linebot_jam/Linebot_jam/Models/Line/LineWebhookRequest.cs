namespace Linebot_jam.Models.Line;

public class LineWebhookRequest
{
    public string? Destination { get; set; }
    public List<LineEvent> Events { get; set; } = new();
}

public class LineEvent
{
    public string Type { get; set; } = string.Empty;
    public string? ReplyToken { get; set; }
    public LineSource? Source { get; set; }
    public long Timestamp { get; set; }
    public LineMessage? Message { get; set; }
}

public class LineSource
{
    public string Type { get; set; } = string.Empty;
    public string? UserId { get; set; }
}

public class LineMessage
{
    public string? Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }
}
