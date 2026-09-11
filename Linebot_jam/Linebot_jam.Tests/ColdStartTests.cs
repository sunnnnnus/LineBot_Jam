using System.Net;
using System.Text;
using System.Text.Json;
using Linebot_jam.Controllers;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Linebot_jam.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Linebot_jam.Tests;

[TestClass]
public class ColdStartTests
{
    [DataTestMethod]
    [DataRow(3861, "約剩 2 天 17 小時到期")]
    [DataRow(2880, "約剩 2 天到期")]
    [DataRow(120, "約剩 2 小時到期")]
    [DataRow(61, "約剩 1 小時 1 分鐘到期")]
    [DataRow(10, "約剩 10 分鐘到期")]
    [DataRow(0, "已到期")]
    public void RemainingTimeUsesReadableUnits(int minutes, string expected) =>
        Assert.AreEqual(expected, ReminderStageSelector.Describe(TimeSpan.FromMinutes(minutes)));

    [DataTestMethod]
    [DataRow(120, "3h_before")]
    [DataRow(180, "3h_before")]
    [DataRow(181, "1d_before")]
    [DataRow(1440, "1d_before")]
    [DataRow(1441, "3d_before")]
    [DataRow(4320, "3d_before")]
    [DataRow(0, "due")]
    [DataRow(-600, "due")]
    public void WakeupSelectsOnlyOneRelevantStage(int minutes, string expected) =>
        Assert.AreEqual(expected, ReminderStageSelector.Select(TimeSpan.FromMinutes(minutes)));

    [TestMethod]
    public void DistantTaskDoesNotSendEarly() =>
        Assert.IsNull(ReminderStageSelector.Select(TimeSpan.FromDays(4)));

    [TestMethod]
    public async Task VerifyDoesNotNeedDatabase()
    {
        var queue = new FakeQueue { Fail = true };
        var controller = Controller("{\"events\":[]}", queue);
        Assert.IsInstanceOfType(await controller.Post(default), typeof(OkResult));
        Assert.AreEqual(0, queue.Calls);
    }

    [TestMethod]
    public async Task DatabaseUnavailableReturns503InsteadOfAcknowledgingLostEvent()
    {
        var result = await Controller(EventBody, new FakeQueue { Fail = true }).Post(default);
        Assert.AreEqual(503, ((StatusCodeResult)result).StatusCode);
    }

    [TestMethod]
    public async Task WebhookAcknowledgesAfterPersistence()
    {
        var queue = new FakeQueue();
        Assert.IsInstanceOfType(await Controller(EventBody, queue).Post(default), typeof(OkResult));
        Assert.AreEqual(1, queue.Calls);
    }

    [TestMethod]
    public async Task InvalidSignatureDoesNotEnqueue()
    {
        var queue = new FakeQueue();
        Assert.IsInstanceOfType(await Controller(EventBody, queue, false).Post(default), typeof(UnauthorizedResult));
        Assert.AreEqual(0, queue.Calls);
    }

    [TestMethod]
    public async Task InvalidReplyTokenFallsBackToPushWithPersistentRetryKey()
    {
        var job = Job();
        var requests = new List<(string Path, string? Key, string Body)>();
        var client = Client(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Line-Retry-Key", out var keys) ? keys.Single() : null,
                await request.Content!.ReadAsStringAsync()));
            return requests.Count == 1
                ? Response(HttpStatusCode.BadRequest, "{\"message\":\"Invalid reply token\"}")
                : Response(HttpStatusCode.OK);
        });
        var result = await client.SendAsync(job, default);
        WebhookBackgroundService.ApplyDeliveryResult(job, result, DateTime.UtcNow);
        Assert.IsTrue(job.UsePush);
        result = await client.SendAsync(job, default);
        Assert.AreEqual(DeliveryResult.Accepted, result);
        Assert.IsNull(requests[0].Key);
        Assert.AreEqual("/v2/bot/message/push", requests[1].Path);
        Assert.AreEqual(job.RetryKey.ToString(), requests[1].Key);
        using var body = JsonDocument.Parse(requests[1].Body);
        Assert.AreEqual("group-1", body.RootElement.GetProperty("to").GetString());
    }

    [TestMethod]
    public async Task ReplyNetworkFailureDoesNotRiskDuplicatePush()
    {
        var job = Job();
        var result = await Client(_ => throw new HttpRequestException("lost response")).SendAsync(job, default);
        WebhookBackgroundService.ApplyDeliveryResult(job, result, DateTime.UtcNow);
        Assert.AreEqual(DeliveryResult.Ambiguous, result);
        Assert.IsTrue(job.Finished);
        Assert.IsFalse(job.UsePush);
    }

    [TestMethod]
    public async Task InvalidMessageDoesNotTriggerPushFallback()
    {
        var result = await Client(_ => Task.FromResult(Response(HttpStatusCode.BadRequest,
            "{\"message\":\"Invalid request body\"}"))).SendAsync(Job(), default);
        Assert.AreEqual(DeliveryResult.Rejected, result);
    }

    [TestMethod]
    public async Task PushRetryReusesKeyAndAcceptsPreviouslyAcceptedRequest()
    {
        var job = Job();
        job.UsePush = true;
        var keys = new List<string>();
        var client = Client(request =>
        {
            keys.Add(request.Headers.GetValues("X-Line-Retry-Key").Single());
            var response = Response(keys.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Conflict);
            if (keys.Count == 2) response.Headers.Add("x-line-accepted-request-id", "original-request");
            return Task.FromResult(response);
        });
        Assert.AreEqual(DeliveryResult.Retryable, await client.SendAsync(job, default));
        Assert.AreEqual(DeliveryResult.Accepted, await client.SendAsync(job, default));
        Assert.AreEqual(keys[0], keys[1]);
    }

    [TestMethod]
    public void GroupFallbackNeverBecomesPrivateMessage() =>
        Assert.IsNull(new LineSource { Type = "group", UserId = "user-1" }.PushDestination);

    private const string EventBody = "{\"events\":[{\"webhookEventId\":\"event-1\",\"type\":\"message\",\"replyToken\":\"token\",\"message\":{\"type\":\"text\",\"text\":\"hi\"}}]}";

    private static LineWebhookController Controller(string body, FakeQueue queue, bool valid = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return new LineWebhookController(new Signature(valid), queue, NullLogger<LineWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    internal static WebhookJob Job() => new()
    {
        EventId = "event-1", Destination = "group-1",
        Payload = JsonSerializer.Serialize(new LineEvent { ReplyToken = "token" }),
        ReplyMessages = "[{\"type\":\"text\",\"text\":\"hello\"}]"
    };

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body) };
    internal static WebhookDeliveryClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new Handler(send)) { BaseAddress = new Uri("https://api.line.me/") },
            NullLogger<WebhookDeliveryClient>.Instance);
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    private sealed class Signature(bool valid) : ILineSignatureValidator
    {
        public bool IsValidSignature(byte[] requestBody, string? signatureHeader) => valid;
    }
    private sealed class FakeQueue : IWebhookQueue
    {
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public Task EnqueueAsync(IEnumerable<LineEvent> events, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new IOException("database unavailable");
            return Task.CompletedTask;
        }
    }
}
