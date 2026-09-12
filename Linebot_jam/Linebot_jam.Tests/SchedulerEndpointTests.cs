using Linebot_jam.Controllers;
using Linebot_jam.Models.Line;
using Linebot_jam.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Linebot_jam.Tests;

[TestClass]
public class SchedulerEndpointTests
{
    [TestMethod]
    public async Task MissingConfigurationFailsClosed()
    {
        var queue = new Queue();
        var result = await Controller(queue, null, "Bearer guessed").Process(default);
        Assert.AreEqual(503, ((ObjectResult)result).StatusCode);
        Assert.AreEqual(0, queue.Events.Count);
    }
    [TestMethod]
    public async Task InvalidTokenDoesNotEnqueue()
    {
        var queue = new Queue();
        Assert.IsInstanceOfType(await Controller(queue, "secret", "Bearer wrong").Process(default), typeof(UnauthorizedResult));
        Assert.AreEqual(0, queue.Events.Count);
    }
    [TestMethod]
    public async Task AuthenticatedRequestAcknowledgesDurableQueue()
    {
        var queue = new Queue();
        var controller = Controller(queue, "secret", "Bearer secret");
        var key = Guid.NewGuid().ToString();
        controller.Request.Headers["X-Scheduler-Run-Id"] = key;
        Assert.IsInstanceOfType(await controller.Process(default), typeof(AcceptedResult));
        Assert.AreEqual("reminder-scan:" + key, queue.Events.Single().WebhookEventId);
        Assert.AreEqual("reminder_scan", queue.Events.Single().Type);
    }
    [TestMethod]
    public async Task UnavailableDatabaseIsNotAcknowledged()
    {
        var result = await Controller(new Queue { Fail = true }, "secret", "Bearer secret").Process(default);
        Assert.AreEqual(503, ((ObjectResult)result).StatusCode);
    }
    private static ReminderController Controller(Queue queue, string? secret, string authorization)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Reminder:SchedulerToken"] = secret }).Build();
        var controller = new ReminderController(queue, config, TimeProvider.System, NullLogger<ReminderController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers.Authorization = authorization;
        return controller;
    }
    private sealed class Queue : IWebhookQueue
    {
        public bool Fail { get; init; }
        public List<LineEvent> Events { get; } = new();
        public Task EnqueueAsync(IEnumerable<LineEvent> events, CancellationToken ct)
        {
            if (Fail) throw new IOException("offline");
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }
}
