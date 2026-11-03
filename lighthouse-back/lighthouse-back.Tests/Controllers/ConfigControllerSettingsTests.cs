using Lighthouse.Controllers;
using Lighthouse.Data;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lighthouse.Tests.Controllers;

public class ConfigControllerSettingsTests
{
    private const string Headers = "Authorization: Bearer secret";

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // Settings endpoints don't use LogLevelService.
    private static ConfigController CreateSut(AppDbContext db) =>
        new(db, NullLogger<ConfigController>.Instance, null!, new ConfigurationBuilder().Build());

    [Fact]
    public async Task GetSettings_MasksWebhookHeaders()
    {
        using AppDbContext db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = WebhookNotificationService.HeadersKey, Value = Headers });
        db.AppSettings.Add(new AppSetting { Key = WebhookNotificationService.UrlKey, Value = "http://apprise:8000/notify/homelab" });
        await db.SaveChangesAsync();

        var result = await CreateSut(db).GetSettings();

        var data = ((ApiResponse<Dictionary<string, string>>)((OkObjectResult)result.Result!).Value!).Data!;
        data[WebhookNotificationService.HeadersKey].Should().Be("••••••••");
        data[WebhookNotificationService.UrlKey].Should().Be("http://apprise:8000/notify/homelab");
    }

    [Fact]
    public async Task UpdateSetting_DoesNotEchoWebhookHeaders()
    {
        using AppDbContext db = CreateDb();

        var result = await CreateSut(db).UpdateSetting(WebhookNotificationService.HeadersKey, new UpdateSettingRequest(Headers));

        var response = (ApiResponse<AppSetting>)((OkObjectResult)result.Result!).Value!;
        response.Data!.Value.Should().Be("••••••••");
        (await db.AppSettings.SingleAsync(s => s.Key == WebhookNotificationService.HeadersKey)).Value.Should().Be(Headers);
    }

    [Fact]
    public async Task UpdateSetting_RejectsInvalidWebhookHeaders()
    {
        using AppDbContext db = CreateDb();

        var result = await CreateSut(db).UpdateSetting(WebhookNotificationService.HeadersKey, new UpdateSettingRequest("Content-Type: x"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.AppSettings.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateSetting_RejectsInvalidWebhookUrl()
    {
        using AppDbContext db = CreateDb();

        var result = await CreateSut(db).UpdateSetting(WebhookNotificationService.UrlKey, new UpdateSettingRequest("not-a-url"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(WebhookNotificationService.UrlKey)]
    [InlineData(WebhookNotificationService.HeadersKey)]
    public async Task UpdateSetting_AllowsEmptyWebhookUrlAndHeaders(string key)
    {
        using AppDbContext db = CreateDb();

        var result = await CreateSut(db).UpdateSetting(key, new UpdateSettingRequest(""));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateSetting_RejectsNonBoolWebhookEnabled()
    {
        using AppDbContext db = CreateDb();

        var result = await CreateSut(db).UpdateSetting(WebhookNotificationService.EnabledKey, new UpdateSettingRequest("yes"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
