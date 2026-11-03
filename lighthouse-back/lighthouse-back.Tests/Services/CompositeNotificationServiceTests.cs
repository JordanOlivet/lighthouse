using Lighthouse.Services.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Lighthouse.Tests.Services;

public class CompositeNotificationServiceTests
{
    private readonly Mock<INotificationService> _first = new();
    private readonly Mock<INotificationService> _second = new();

    private CompositeNotificationService CreateSut() =>
        new(new[] { _first.Object, _second.Object }, NullLogger<CompositeNotificationService>.Instance);

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
