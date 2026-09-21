namespace Linebot_jam.Services.Mail;

public sealed record SchoolMail(string Id, string Subject, string Body, DateTimeOffset ReceivedAt);
public sealed record MailPage(IReadOnlyList<string> Ids, string? NextPageToken);
public interface IGmailReader
{
    Task<string> GetAccountAsync(CancellationToken ct);
    Task<MailPage> ListAsync(DateTimeOffset since, string? pageToken, CancellationToken ct);
    Task<SchoolMail?> ReadAsync(string id, CancellationToken ct);
}
public interface IMailClassifier
{
    Task<IReadOnlyList<MailDecision>> ClassifyAsync(SchoolMail mail, CancellationToken ct);
}
public sealed class MailDecision
{
    public string Kind { get; set; } = ""; // task or notice
    public string Content { get; set; } = "";
    public string? DueAt { get; set; }
    public string? EventDate { get; set; }
}
public static class MailTime
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    public static DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Taipei).DateTime;
    public static DateTime? ParseDue(string? text) => DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
}
