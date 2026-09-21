using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Linebot_jam.Options;
using Microsoft.Extensions.Options;

namespace Linebot_jam.Services.Mail;

public sealed class GmailReader(HttpClient http, IOptions<GmailOptions> options) : IGmailReader
{
    public const string Sender = "elearn@mail.fju.edu.tw";
    private string? _accessToken;
    private DateTimeOffset _expires;

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        if (_accessToken is null || DateTimeOffset.UtcNow >= _expires)
        {
            var config = options.Value;
            using var tokenResponse = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret,
                ["refresh_token"] = config.RefreshToken, ["grant_type"] = "refresh_token"
            }), ct);
            if (!tokenResponse.IsSuccessStatusCode) throw new HttpRequestException($"Gmail authorization failed ({(int)tokenResponse.StatusCode}); reconnect Gmail.");
            using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
            _accessToken = token.RootElement.GetProperty("access_token").GetString();
            _expires = DateTimeOffset.UtcNow.AddSeconds(token.RootElement.GetProperty("expires_in").GetInt32() - 60);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<string> GetAccountAsync(CancellationToken ct)
    {
        using var profile = await GetAsync("profile", ct);
        return profile.RootElement.GetProperty("emailAddress").GetString()!;
    }

    public async Task<MailPage> ListAsync(DateTimeOffset since, string? pageToken, CancellationToken ct)
    {
        var query = $"from:{Sender} {{subject:作業 subject:公告}} after:{since.ToUnixTimeSeconds()}";
        using var page = await GetAsync("messages?maxResults=100&q=" + Uri.EscapeDataString(query)
            + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken)), ct);
        var ids = page.RootElement.TryGetProperty("messages", out var messages)
            ? messages.EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray() : Array.Empty<string>();
        return new MailPage(ids, page.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null);
    }

    public async Task<SchoolMail?> ReadAsync(string id, CancellationToken ct)
    {
        using var message = await GetAsync("messages/" + Uri.EscapeDataString(id) + "?format=full", ct);
        return Parse(message.RootElement);
    }

    public static SchoolMail? Parse(JsonElement message)
    {
        var payload = message.GetProperty("payload");
        string Header(string name) => payload.GetProperty("headers").EnumerateArray()
            .FirstOrDefault(h => string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            is var header && header.ValueKind == JsonValueKind.Object ? header.GetProperty("value").GetString() ?? "" : "";
        if (!MailAddress.TryCreate(Header("From"), out var sender) || !sender.Address.Equals(Sender, StringComparison.OrdinalIgnoreCase)) return null;
        var subject = DecodeHeader(Header("Subject"));
        if (!subject.Contains("作業") && !subject.Contains("公告")) return null;
        var text = Extract(payload, "text/plain");
        if (string.IsNullOrWhiteSpace(text))
        {
            var html = Extract(payload, "text/html");
            html = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            html = Regex.Replace(html, @"<[^>]+>", "\n", RegexOptions.None, TimeSpan.FromSeconds(1));
            text = WebUtility.HtmlDecode(html);
        }
        if (string.IsNullOrWhiteSpace(text)) text = "[郵件本文無法讀取，請使用者查看原信，不得根據主旨猜測期限]";
        if (text.Length > 40000) text = text[..40000] + "\n[本文過長已截斷；可能缺少資訊，須請使用者查看原信]";
        return new SchoolMail(message.GetProperty("id").GetString()!, subject, text,
            DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(message.GetProperty("internalDate").GetString()!)));
    }

    private static string DecodeHeader(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // RFC 2047: Chinese subjects may arrive encoded rather than as literal Chinese.
        value = Regex.Replace(value, @"(?<=\?=)\s+(?==\?)", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Regex.Replace(value, @"=\?([^?]+)\?([bBqQ])\?([^?]*)\?=", match =>
        {
            var encoded = match.Groups[3].Value;
            byte[] bytes;
            if (match.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase)) bytes = Convert.FromBase64String(encoded);
            else
            {
                var decoded = new List<byte>();
                for (var i = 0; i < encoded.Length; i++)
                {
                    if (encoded[i] == '=' && i + 2 < encoded.Length) { decoded.Add(Convert.ToByte(encoded.Substring(i + 1, 2), 16)); i += 2; }
                    else decoded.Add((byte)(encoded[i] == '_' ? ' ' : encoded[i]));
                }
                bytes = decoded.ToArray();
            }
            return Encoding.GetEncoding(match.Groups[1].Value).GetString(bytes);
        }, RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static string Extract(JsonElement part, string mime)
    {
        if (part.TryGetProperty("filename", out var filename) && !string.IsNullOrEmpty(filename.GetString())) return "";
        if (part.TryGetProperty("mimeType", out var type) && type.GetString() == mime &&
            part.TryGetProperty("body", out var body) && body.TryGetProperty("data", out var data))
        {
            var encoded = (data.GetString() ?? "").Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        return part.TryGetProperty("parts", out var parts)
            ? string.Join("\n", parts.EnumerateArray().Select(p => Extract(p, mime)).Where(x => x.Length > 0)) : "";
    }
}
