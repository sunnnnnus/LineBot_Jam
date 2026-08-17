namespace Linebot_jam.Services;

public interface ILineMessagingClient
{
    Task ReplyMessageAsync(string replyToken, string text, CancellationToken cancellationToken = default);
    Task ReplyWithLinkButtonAsync(string replyToken, string text, string buttonLabel, string url, CancellationToken cancellationToken = default);
    Task<bool> PushMessageAsync(string userId, string text, CancellationToken cancellationToken = default);
}
