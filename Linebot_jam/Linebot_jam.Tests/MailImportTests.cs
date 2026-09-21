using System.Text;
using System.Text.Json;
using Linebot_jam.Services.Mail;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Linebot_jam.Tests;

[TestClass]
public class MailImportTests
{
    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static JsonElement Message(string from, string subject, object payloadParts) => JsonSerializer.SerializeToElement(new
    {
        id = "mail-1", internalDate = "1789261200000",
        payload = new { mimeType = "multipart/alternative", headers = new[] { new { name = "From", value = from }, new { name = "Subject", value = subject } }, parts = payloadParts }
    });

    [TestMethod]
    public void PrefersPlainTextAndDoesNotReadAttachments()
    {
        var message = Message("TronClass <elearn@mail.fju.edu.tw>", "英文作業", new[]
        {
            new { mimeType = "text/plain", filename = "", body = new { data = Encode("週五交英文作業") } },
            new { mimeType = "text/html", filename = "", body = new { data = Encode("<p>另一版本</p>") } },
            new { mimeType = "text/plain", filename = "secret.txt", body = new { data = Encode("附件不讀") } }
        });
        Assert.AreEqual("週五交英文作業", GmailReader.Parse(message)!.Body);
    }

    [TestMethod]
    public void ReadsHtmlOnlyAnnouncementsWithoutScripts()
    {
        var message = Message("elearn@mail.fju.edu.tw", "停課公告", new[]
        {
            new { mimeType = "text/html", filename = "", body = new { data = Encode("<style>hidden</style><p>9/22 停課&nbsp;一次</p><script>bad()</script>") } }
        });
        var body = GmailReader.Parse(message)!.Body;
        StringAssert.Contains(body, "9/22 停課");
        Assert.IsFalse(body.Contains("bad()") || body.Contains("hidden"));
    }

    [TestMethod]
    public void DecodesMimeEncodedChineseSubjects()
    {
        var encoded = "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("英文作業")) + "?=";
        Assert.AreEqual("英文作業", GmailReader.Parse(Message("elearn@mail.fju.edu.tw", encoded, Array.Empty<object>()))!.Subject);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    [TestMethod]
    public async Task GmailRefreshesTokenAndPagesWithTheRestrictedQuery()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            string body;
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                StringAssert.Contains(await request.Content!.ReadAsStringAsync(), "grant_type=refresh_token");
                body = """{"access_token":"test-token","expires_in":3600}""";
            }
            else
            {
                Assert.AreEqual("test-token", request.Headers.Authorization!.Parameter);
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                StringAssert.Contains(query, "from:elearn@mail.fju.edu.tw");
                StringAssert.Contains(query, "{subject:作業 subject:公告}");
                StringAssert.Contains(query, "pageToken=next-page");
                body = """{"messages":[{"id":"mail-1"}],"nextPageToken":"page-3"}""";
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var reader = new GmailReader(http, Microsoft.Extensions.Options.Options.Create(new Linebot_jam.Options.GmailOptions
        { ClientId = "client", ClientSecret = "secret", RefreshToken = "refresh" }));
        var page = await reader.ListAsync(DateTimeOffset.UtcNow, "next-page", default);
        Assert.AreEqual("page-3", page.NextPageToken);
        Assert.AreEqual("mail-1", page.Ids.Single());
        await reader.ListAsync(DateTimeOffset.UtcNow, "next-page", default);
        Assert.AreEqual(3, calls); // Token is reused within a synchronization cycle.
    }

    [DataTestMethod]
    [DataRow("elearn@mail.fju.edu.tw.evil.com", "作業")]
    [DataRow("elearn@mail.fju.edu.tw", "廣告")]
    public void RechecksExactSenderAndSubject(string from, string subject)
    {
        Assert.IsNull(GmailReader.Parse(Message(from, subject, Array.Empty<object>())));
    }

    [TestMethod]
    public void AcceptsMixedDecisionsAndMissingDeadline()
    {
        var result = GroqMailClassifier.Parse("""{"items":[{"kind":"notice","content":"明日停課","eventDate":"2026-09-22"},{"kind":"task","content":"繳交英文作業","dueAt":null}]}""");
        Assert.AreEqual(2, result.Count);
        Assert.IsNull(result[1].DueAt);
    }

    [DataTestMethod]
    [DataRow("{\"items\":[{\"kind\":\"confirm_task\",\"content\":\"新增\"}]}")]
    [DataRow("{\"items\":[{\"kind\":\"task\",\"content\":\"作業\",\"dueAt\":\"下週\"}]}")]
    [DataRow("{\"items\":[{\"kind\":\"notice\",\"content\":\"停課\",\"eventDate\":\"tomorrow\"}]}")]
    public void RejectsInvalidAiOutput(string json) => Assert.ThrowsException<JsonException>(() => GroqMailClassifier.Parse(json));
}
