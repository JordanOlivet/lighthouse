using Lighthouse.Data;
using Lighthouse.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Lighthouse.Tests.Services;

public class CompositeNotificationServiceTests
{
    private readonly Mock<INotificationService> _first = new();
    private readonly Mock<INotificationService> _second = new();

    private CompositeNotificationService CreateSut()
    {
        IServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        return new CompositeNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Func<IServiceProvider, INotificationService>[] { _ => _first.Object, _ => _second.Object },
            NullLogger<CompositeNotificationService>.Instance);
    }

    [Fact]
    public async Task Notify_ResolvesEachChannelInItsOwnScope_SoChannelsNeverShareADbContext()
    {
        // Channels run concurrently; EF Core's DbContext is not thread-safe, so each
        // channel must get its own scoped AppDbContext.
        var services = new ServiceCollection();
        string dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        using ServiceProvider provider = services.BuildServiceProvider();
        var contexts = new List<AppDbContext>();
        INotificationService Capture(IServiceProvider sp)
        {
            contexts.Add(sp.GetRequiredService<AppDbContext>());
            return new Mock<INotificationService>().Object;
        }
        var sut = new CompositeNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Func<IServiceProvider, INotificationService>[] { Capture, Capture },
            NullLogger<CompositeNotificationService>.Instance);

        await sut.NotifyAppUpdateCompletedAsync("2");

        contexts.Should().HaveCount(2);
        contexts[0].Should().NotBeSameAs(contexts[1]);
    }

    [Fact]
    public async Task Notify_CallsAllChannels()
    {
        await CreateSut().NotifyAppUpdateStartedAsync("1", "2");

        _first.Verify(c => c.NotifyAppUpdateStartedAsync("1", "2", It.IsAny<CancellationToken>()), Times.Once);
        _second.Verify(c => c.NotifyAppUpdateStartedAsync("1", "2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Notify_CallsAllChannels_WhenOneThrowsSynchronously()
    {
        _first.Setup(c => c.NotifyComposeAutoUpdateAsync(It.IsAny<ComposeAutoUpdateReport>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("sync boom"));

        Func<Task> act = () => CreateSut().NotifyComposeAutoUpdateAsync(new ComposeAutoUpdateReport());

        await act.Should().NotThrowAsync();
        _second.Verify(c => c.NotifyComposeAutoUpdateAsync(It.IsAny<ComposeAutoUpdateReport>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Notify_CallsAllChannels_WhenOneFaults()
    {
        _first.Setup(c => c.NotifyComposeAutoUpdateAsync(It.IsAny<ComposeAutoUpdateReport>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("async boom"));

        Func<Task> act = () => CreateSut().NotifyComposeAutoUpdateAsync(new ComposeAutoUpdateReport());

        await act.Should().NotThrowAsync();
        _second.Verify(c => c.NotifyComposeAutoUpdateAsync(It.IsAny<ComposeAutoUpdateReport>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Notify_RunsChannelsConcurrently()
    {
        var pending = new TaskCompletionSource();
        _first.Setup(c => c.NotifyAppUpdateStartedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        _second.Setup(c => c.NotifyAppUpdateStartedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Task running = CreateSut().NotifyAppUpdateStartedAsync("1", "2");

        _second.Verify(c => c.NotifyAppUpdateStartedAsync("1", "2", It.IsAny<CancellationToken>()), Times.Once);
        running.IsCompleted.Should().BeFalse();
        pending.SetResult();
        await running;
    }

    [Fact]
    public async Task NotifyAppUpdateFailed_ForwardsArguments()
    {
        await CreateSut().NotifyAppUpdateFailedAsync("1", "2", "err");

        _first.Verify(c => c.NotifyAppUpdateFailedAsync("1", "2", "err", It.IsAny<CancellationToken>()), Times.Once);
        _second.Verify(c => c.NotifyAppUpdateFailedAsync("1", "2", "err", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyAppUpdateCompleted_ForwardsArguments()
    {
        await CreateSut().NotifyAppUpdateCompletedAsync("2");

        _first.Verify(c => c.NotifyAppUpdateCompletedAsync("2", It.IsAny<CancellationToken>()), Times.Once);
        _second.Verify(c => c.NotifyAppUpdateCompletedAsync("2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyImagePrune_ForwardsArguments()
    {
        var images = new List<string> { "a:1" };

        await CreateSut().NotifyImagePruneAsync(images, 42, true);

        _first.Verify(c => c.NotifyImagePruneAsync(images, 42, true, It.IsAny<CancellationToken>()), Times.Once);
        _second.Verify(c => c.NotifyImagePruneAsync(images, 42, true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
