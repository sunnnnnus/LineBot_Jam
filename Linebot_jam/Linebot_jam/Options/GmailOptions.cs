namespace Linebot_jam.Options;

public sealed class GmailOptions
{
    public bool Enabled { get; set; }
    public string AccountEmail { get; set; } = "";
    public string OwnerLineUserId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public int PollMinutes { get; set; } = 5;
    // Optional explicit backfill boundary; otherwise first successful sync starts at now.
    public DateTimeOffset? StartAtUtc { get; set; }
    public bool IsConfigured => new[] { AccountEmail, OwnerLineUserId, ClientId, ClientSecret, RefreshToken }
        .All(value => !string.IsNullOrWhiteSpace(value));
}
