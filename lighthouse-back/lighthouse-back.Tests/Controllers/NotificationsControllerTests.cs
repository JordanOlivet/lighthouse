using System.Net;
using Lighthouse.Controllers;
using Lighthouse.Data;
using Lighthouse.Services.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lighthouse.Tests.Controllers;

public class NotificationsControllerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public int CallCount { get; private set; }

        public StubHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("") });
        }
    }

    private static (NotificationsController controller, StubHandler handler) CreateSut(HttpStatusCode status = HttpStatusCode.OK)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var handler = new StubHandler(status);
        var discord = new DiscordNotificationService(new HttpClient(handler), db, NullLogger<DiscordNotificationService>.Instance);
        var webhook = new WebhookNotificationService(new HttpClient(handler), db, NullLogger<WebhookNotificationService>.Instance);
        return (new NotificationsController(discord, webhook, NullLogger<NotificationsController>.Instance), handler);
    }

    [Fact]
    public async Task TestWebhook_ReturnsBadRequest_ForInvalidUrl()
    {
        (NotificationsController controller, StubHandler handler) = CreateSut();

        var result = await controller.SendWebhookTest(new TestWebhookRequest("ftp://x"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task TestWebhook_ReturnsBadRequest_ForInvalidHeaders()
    {
        (NotificationsController controller, StubHandler handler) = CreateSut();

        var result = await controller.SendWebhookTest(
            new TestWebhookRequest("http://apprise:8000/notify/homelab", "nocolon"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task TestWebhook_ReturnsOk_WhenReceiverAccepts()
    {
        (NotificationsController controller, StubHandler handler) = CreateSut();

        var result = await controller.SendWebhookTest(
            new TestWebhookRequest("http://apprise:8000/notify/homelab"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task TestWebhook_ReturnsBadRequest_WhenReceiverFails()
    {
        (NotificationsController controller, _) = CreateSut(HttpStatusCode.InternalServerError);

        var result = await controller.SendWebhookTest(
            new TestWebhookRequest("http://apprise:8000/notify/homelab"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestDiscord_StillValidatesDiscordUrl()
    {
        (NotificationsController controller, StubHandler handler) = CreateSut();

        var result = await controller.SendTest(
            new TestNotificationRequest("https://evil.com/api/webhooks/1/t"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        handler.CallCount.Should().Be(0);
    }
}
