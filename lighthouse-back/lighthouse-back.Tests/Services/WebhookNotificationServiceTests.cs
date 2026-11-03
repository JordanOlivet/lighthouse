using System.Net;
using System.Text.Json;
using Lighthouse.Data;
using Lighthouse.Models;
using Lighthouse.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lighthouse.Tests.Services;

public class WebhookNotificationServiceTests
{
    private const string Url = "http://apprise:8000/notify/homelab";

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task SeedSettingsAsync(AppDbContext db, bool? enabled, string? url, string? headers = null)
    {
        if (enabled.HasValue)
        {
            db.AppSettings.Add(new AppSetting { Key = WebhookNotificationService.EnabledKey, Value = enabled.Value ? "true" : "false" });
        }
        if (url != null)
        {
            db.AppSettings.Add(new AppSetting { Key = WebhookNotificationService.UrlKey, Value = url });
        }
        if (headers != null)
        {
            db.AppSettings.Add(new AppSetting { Key = WebhookNotificationService.HeadersKey, Value = headers });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>A request captured by <see cref="RecordingHandler"/>.</summary>
    private sealed record CapturedRequest(Uri? Uri, string Body, Dictionary<string, string> Headers);

    /// <summary>Captures requests sent through the HttpClient and returns a fixed status (or throws).</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Exception? _throw;
        public List<CapturedRequest> Requests { get; } = new();
        public int CallCount => Requests.Count;

        public RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, Exception? toThrow = null)
        {
            _status = status;
            _throw = toThrow;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            string body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri, body, headers));
            if (_throw != null)
            {
                throw _throw;
            }
            return new HttpResponseMessage(_status) { Content = new StringContent("receiver says no") };
        }
    }

    private static (WebhookNotificationService service, RecordingHandler handler) CreateService(
        AppDbContext db, HttpStatusCode status = HttpStatusCode.OK, Exception? toThrow = null)
    {
        var handler = new RecordingHandler(status, toThrow);
        var service = new WebhookNotificationService(new HttpClient(handler), db, new NullLogger<WebhookNotificationService>());
        return (service, handler);
    }

    private static JsonElement Payload(RecordingHandler handler, int index = 0) =>
        JsonDocument.Parse(handler.Requests[index].Body).RootElement;

    private static ComposeAutoUpdateReport Report(int updated, int failed)
    {
        var report = new ComposeAutoUpdateReport();
        for (int i = 0; i < updated; i++)
        {
            report.Projects.Add(new ProjectUpdateResult { ProjectName = $"ok{i}", Success = true });
        }
        for (int i = 0; i < failed; i++)
        {
            report.Projects.Add(new ProjectUpdateResult { ProjectName = $"ko{i}", Success = false, Error = "boom" });
        }
        return report;
    }

    [Fact]
    public async Task Notify_DoesNothing_WhenDisabled()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: false, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyAppUpdateStartedAsync("1.0.0", "1.1.0");

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Notify_DoesNothing_WhenUrlMissing()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: null);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyAppUpdateStartedAsync("1.0.0", "1.1.0");

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AppUpdateStarted_SendsExpectedPayload()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyAppUpdateStartedAsync("1.0.0", "1.1.0");

        handler.CallCount.Should().Be(1);
        handler.Requests[0].Uri.Should().Be(new Uri(Url));
        JsonElement p = Payload(handler);
        p.GetProperty("title").GetString().Should().Be("🚀 Application update started");
        p.GetProperty("type").GetString().Should().Be("info");
        p.GetProperty("event").GetString().Should().Be("app.update.started");
        p.GetProperty("body").GetString().Should().Contain("1.0.0").And.Contain("1.1.0");
        p.GetProperty("data").GetProperty("fromVersion").GetString().Should().Be("1.0.0");
        p.GetProperty("data").GetProperty("toVersion").GetString().Should().Be("1.1.0");
        DateTimeOffset.TryParse(p.GetProperty("timestamp").GetString(), out _).Should().BeTrue();
    }

    [Fact]
    public async Task AppUpdateFailed_SendsFailureWithError()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyAppUpdateFailedAsync("1.0.0", "1.1.0", "boom");

        JsonElement p = Payload(handler);
        p.GetProperty("type").GetString().Should().Be("failure");
        p.GetProperty("event").GetString().Should().Be("app.update.failed");
        p.GetProperty("data").GetProperty("error").GetString().Should().Be("boom");
        p.GetProperty("body").GetString().Should().Contain("boom");
    }

    [Fact]
    public async Task AppUpdateCompleted_SendsSuccess()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyAppUpdateCompletedAsync("1.1.0");

        JsonElement p = Payload(handler);
        p.GetProperty("type").GetString().Should().Be("success");
        p.GetProperty("event").GetString().Should().Be("app.update.completed");
        p.GetProperty("data").GetProperty("version").GetString().Should().Be("1.1.0");
    }

    [Theory]
    [InlineData(2, 0, "success")]
    [InlineData(1, 1, "warning")]
    [InlineData(0, 2, "failure")]
    public async Task ComposeAutoUpdate_TypeMapping(int updated, int failed, string expectedType)
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyComposeAutoUpdateAsync(Report(updated, failed));

        JsonElement p = Payload(handler);
        p.GetProperty("type").GetString().Should().Be(expectedType);
        p.GetProperty("event").GetString().Should().Be("compose.autoupdate");
        p.GetProperty("data").GetProperty("updatedCount").GetInt32().Should().Be(updated);
        p.GetProperty("data").GetProperty("failedCount").GetInt32().Should().Be(failed);
    }

    [Fact]
    public async Task ComposeAutoUpdate_IncludesProjectsAndServices()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);
        var report = new ComposeAutoUpdateReport();
        report.Projects.Add(new ProjectUpdateResult
        {
            ProjectName = "coder",
            Success = true,
            Services =
            {
                new ServiceChange
                {
                    ServiceName = "coder",
                    Image = "ghcr.io/coder/coder:latest",
                    OldDigestShort = "a1b2c3d4e5f6",
                    NewDigestShort = "0f9e8d7c6b5a"
                }
            }
        });
        report.Projects.Add(new ProjectUpdateResult { ProjectName = "authelia", Success = false, Error = "pull failed" });

        await service.NotifyComposeAutoUpdateAsync(report);

        JsonElement p = Payload(handler);
        JsonElement projects = p.GetProperty("data").GetProperty("projects");
        projects[0].GetProperty("name").GetString().Should().Be("coder");
        projects[0].GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        JsonElement svc = projects[0].GetProperty("services")[0];
        svc.GetProperty("name").GetString().Should().Be("coder");
        svc.GetProperty("image").GetString().Should().Be("ghcr.io/coder/coder:latest");
        svc.GetProperty("oldDigest").GetString().Should().Be("a1b2c3d4e5f6");
        svc.GetProperty("newDigest").GetString().Should().Be("0f9e8d7c6b5a");
        projects[1].GetProperty("success").GetBoolean().Should().BeFalse();
        projects[1].GetProperty("error").GetString().Should().Be("pull failed");
        string body = p.GetProperty("body").GetString()!;
        body.Should().Contain("✅ coder").And.Contain("❌ authelia").And.Contain("a1b2c3d4e5f6 → 0f9e8d7c6b5a");
    }

    [Fact]
    public async Task ComposeAutoUpdate_IncludesAllProjects_BeyondDiscordLimit()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyComposeAutoUpdateAsync(Report(30, 0));

        Payload(handler).GetProperty("data").GetProperty("projects").GetArrayLength().Should().Be(30);
    }

    [Fact]
    public async Task ComposeAutoUpdate_DoesNothing_WhenReportEmpty()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyComposeAutoUpdateAsync(new ComposeAutoUpdateReport());

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ImagePrune_SendsAllImagesInData()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);
        List<string> images = Enumerable.Range(0, 25).Select(i => $"repo/img{i}:latest").ToList();

        await service.NotifyImagePruneAsync(images, 2048, danglingOnly: true);

        JsonElement p = Payload(handler);
        p.GetProperty("type").GetString().Should().Be("success");
        p.GetProperty("event").GetString().Should().Be("images.prune");
        JsonElement data = p.GetProperty("data");
        data.GetProperty("images").GetArrayLength().Should().Be(25);
        data.GetProperty("count").GetInt32().Should().Be(25);
        data.GetProperty("spaceReclaimedBytes").GetInt64().Should().Be(2048);
        data.GetProperty("danglingOnly").GetBoolean().Should().BeTrue();
        p.GetProperty("body").GetString().Should().Contain("… +5 more");
    }

    [Fact]
    public async Task Notify_SendsSavedHeaders()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url, headers: "Authorization: Bearer abc\nX-Api-Key: k1");
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.NotifyAppUpdateCompletedAsync("1.1.0");

        handler.Requests[0].Headers.Should().Contain("Authorization", "Bearer abc");
        handler.Requests[0].Headers.Should().Contain("X-Api-Key", "k1");
    }

    [Fact]
    public async Task Notify_DoesNotThrow_WhenReceiverReturns500()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db, HttpStatusCode.InternalServerError);

        Func<Task> act = () => service.NotifyAppUpdateCompletedAsync("1.1.0");

        await act.Should().NotThrowAsync();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Notify_DoesNotThrow_WhenRequestTimesOut()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, _) = CreateService(db, toThrow: new TaskCanceledException("timeout"));

        Func<Task> act = () => service.NotifyAppUpdateCompletedAsync("1.1.0");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendTest_UsesOverrideUrl_EvenWhenDisabled()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: false, url: null);
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        NotificationTestResult result = await service.SendTestAsync("https://example.com/hook", null);

        result.Success.Should().BeTrue();
        handler.Requests[0].Uri.Should().Be(new Uri("https://example.com/hook"));
        JsonElement p = Payload(handler);
        p.GetProperty("event").GetString().Should().Be("test");
        p.GetProperty("type").GetString().Should().Be("info");
    }

    [Fact]
    public async Task SendTest_UsesOverrideUrl_WithSavedHeaders()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url, headers: "X-Api-Key: k1");
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.SendTestAsync("https://example.com/hook", overrideHeaders: null);

        handler.Requests[0].Headers.Should().Contain("X-Api-Key", "k1");
    }

    [Fact]
    public async Task SendTest_EmptyOverrideHeaders_SendsNoHeaders()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url, headers: "X-Api-Key: k1");
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        await service.SendTestAsync(null, overrideHeaders: "");

        handler.Requests[0].Uri.Should().Be(new Uri(Url));
        handler.Requests[0].Headers.Should().NotContainKey("X-Api-Key");
    }

    [Fact]
    public async Task SendTest_ReturnsError_WhenNoUrl()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        (WebhookNotificationService service, RecordingHandler handler) = CreateService(db);

        NotificationTestResult result = await service.SendTestAsync(null, null);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("No webhook URL configured.");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SendTest_ReturnsError_WhenReceiverRejects()
    {
        using AppDbContext db = CreateInMemoryDbContext();
        await SeedSettingsAsync(db, enabled: true, url: Url);
        (WebhookNotificationService service, _) = CreateService(db, HttpStatusCode.BadRequest);

        NotificationTestResult result = await service.SendTestAsync(null, null);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("400");
    }
}
