using Lighthouse.Configuration;
using Lighthouse.Models;
using Lighthouse.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>
/// Unit tests for ComposeFileCacheService: stale-while-revalidate reads, shared in-flight
/// scans, invalidation generations and change notifications.
/// </summary>
public class ComposeFileCacheServiceTests
{
    private const int TtlSeconds = 10;

    private readonly Mock<IComposeFileScanner> _mockScanner = new();
    private readonly SseConnectionManagerService _sseManager = new(new NullLogger<SseConnectionManagerService>());
    private readonly ManualTimeProvider _time = new();
    private readonly ComposeFileCacheService _cacheService;

    public ComposeFileCacheServiceTests()
    {
        _cacheService = new ComposeFileCacheService(
            _mockScanner.Object,
            Options.Create(new ComposeDiscoveryOptions { CacheDurationSeconds = TtlSeconds }),
            _sseManager,
            new NullLogger<ComposeFileCacheService>(),
            _time);
    }

    private static List<DiscoveredComposeFile> Files(params string[] projects) =>
        projects.Select(p => new DiscoveredComposeFile
        {
            FilePath = $"/app/{p}/compose.yml",
            ProjectName = p,
            IsValid = true,
            Services = new List<string> { "web" }
        }).ToList();

    private void ExpireTtl() => _time.Advance(TimeSpan.FromSeconds(TtlSeconds + 1));

    /// <summary>Scanner whose scans block until released, returning queued results in order.</summary>
    private sealed class GatedScanner
    {
        private readonly Queue<List<DiscoveredComposeFile>> _results = new();
        private readonly List<TaskCompletionSource> _gates = new();
        public int Calls;

        public GatedScanner(Mock<IComposeFileScanner> mock, params List<DiscoveredComposeFile>[] results)
        {
            foreach (var r in results) _results.Enqueue(r);
            mock.Setup(s => s.ScanComposeFilesAsync()).Returns(async () =>
            {
                TaskCompletionSource gate;
                List<DiscoveredComposeFile> result;
                lock (_gates)
                {
                    Calls++;
                    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Add(gate);
                    result = _results.Dequeue();
                }
                await gate.Task;
                return result;
            });
        }

        public void Release(int index) { lock (_gates) _gates[index].SetResult(); }

        public async Task WaitForCallsAsync(int count)
        {
            for (int i = 0; i < 200 && Volatile.Read(ref Calls) < count; i++) await Task.Delay(10);
            Volatile.Read(ref Calls).Should().BeGreaterThanOrEqualTo(count);
        }
    }

    private SseClient AddFakeSseClient()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var client = new SseClient
        {
            Response = context.Response,
            ConnectionId = Guid.NewGuid().ToString(),
            CancellationToken = CancellationToken.None
        };
        _sseManager.AddClient(client);
        return client;
    }

    private static string ReadBody(SseClient client)
    {
        client.Response.Body.Position = 0;
        using var reader = new StreamReader(client.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    /// <summary>Waits for the background refresh to finish (next read is fresh again).</summary>
    private async Task WaitForScanCountAsync(int count)
    {
        for (int i = 0; i < 200; i++)
        {
            if (_mockScanner.Invocations.Count >= count) break;
            await Task.Delay(10);
        }
        _mockScanner.Invocations.Count.Should().BeGreaterThanOrEqualTo(count);
        await Task.Delay(50); // let the store + notify continuation complete
    }

    [Fact]
    public async Task GetOrScanAsync_NoSnapshot_AwaitsScan()
    {
        _mockScanner.Setup(s => s.ScanComposeFilesAsync()).ReturnsAsync(Files("a"));

        var result = await _cacheService.GetOrScanAsync();

        result.Should().BeEquivalentTo(Files("a"));
        _mockScanner.Verify(s => s.ScanComposeFilesAsync(), Times.Once);
    }

    [Fact]
    public async Task GetOrScanAsync_FreshSnapshot_DoesNotScan()
    {
        _mockScanner.Setup(s => s.ScanComposeFilesAsync()).ReturnsAsync(Files("a"));
        await _cacheService.GetOrScanAsync();

        await _cacheService.GetOrScanAsync();
        await _cacheService.GetOrScanAsync();

        _mockScanner.Verify(s => s.ScanComposeFilesAsync(), Times.Once);
    }

    [Fact]
    public async Task GetOrScanAsync_StaleSnapshot_ReturnsImmediatelyWithoutWaitingForRescan()
    {
        var scanner = new GatedScanner(_mockScanner, Files("a"), Files("a", "b"));
        Task<List<DiscoveredComposeFile>> first = _cacheService.GetOrScanAsync();
        await scanner.WaitForCallsAsync(1);
        scanner.Release(0);
        await first;
        ExpireTtl();

        // The rescan is blocked: a stale read must still complete right away with the old data.
        Task<List<DiscoveredComposeFile>> staleRead = _cacheService.GetOrScanAsync();
        Task completed = await Task.WhenAny(staleRead, Task.Delay(TimeSpan.FromSeconds(2)));

        completed.Should().BeSameAs(staleRead);
        (await staleRead).Should().BeEquivalentTo(Files("a"));
        await scanner.WaitForCallsAsync(2);

        // Once the background rescan finishes, reads see the new snapshot.
        scanner.Release(1);
        await Task.Delay(50);
        (await _cacheService.GetOrScanAsync()).Should().BeEquivalentTo(Files("a", "b"));
    }

    [Fact]
    public async Task GetOrScanAsync_ManyStaleReads_StartSingleBackgroundRescan()
    {
        var scanner = new GatedScanner(_mockScanner, Files("a"), Files("a"));
        Task<List<DiscoveredComposeFile>> first = _cacheService.GetOrScanAsync();
        await scanner.WaitForCallsAsync(1);
        scanner.Release(0);
        await first;
        ExpireTtl();

        for (int i = 0; i < 10; i++)
        {
            await _cacheService.GetOrScanAsync();
        }
        await scanner.WaitForCallsAsync(2);
        await Task.Delay(50);

        scanner.Calls.Should().Be(2);
        scanner.Release(1);
    }

    [Fact]
    public async Task GetOrScanAsync_ConcurrentColdReads_ShareOneScan()
    {
        int calls = 0;
        _mockScanner.Setup(s => s.ScanComposeFilesAsync()).Returns(async () =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(100);
            return Files("a");
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _cacheService.GetOrScanAsync()));

        results.Should().AllSatisfy(r => r.Should().BeEquivalentTo(Files("a")));
        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrScanAsync_BypassCache_ForcesRescan()
    {
        _mockScanner.SetupSequence(s => s.ScanComposeFilesAsync())
            .ReturnsAsync(Files("first"))
            .ReturnsAsync(Files("second"));

        var firstResult = await _cacheService.GetOrScanAsync();
        var secondResult = await _cacheService.GetOrScanAsync(bypassCache: true);

        firstResult.Should().BeEquivalentTo(Files("first"));
        secondResult.Should().BeEquivalentTo(Files("second"));
    }

    [Fact]
    public async Task Invalidate_NextReadAwaitsFreshScan()
    {
        _mockScanner.SetupSequence(s => s.ScanComposeFilesAsync())
            .ReturnsAsync(Files("a"))
            .ReturnsAsync(Files("a", "b"));
        await _cacheService.GetOrScanAsync();

        _cacheService.Invalidate();
        var result = await _cacheService.GetOrScanAsync();

        result.Should().BeEquivalentTo(Files("a", "b"));
        _mockScanner.Verify(s => s.ScanComposeFilesAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task Invalidate_DuringInFlightScan_DoesNotReuseOrStoreOldScan()
    {
        // Scan #1 started before the invalidation (e.g. before a file save) returns old data.
        var scanner = new GatedScanner(_mockScanner, Files("old"), Files("new"));
        Task<List<DiscoveredComposeFile>> beforeInvalidate = _cacheService.GetOrScanAsync();
        await scanner.WaitForCallsAsync(1);

        _cacheService.Invalidate();
        Task<List<DiscoveredComposeFile>> afterInvalidate = _cacheService.GetOrScanAsync();
        await scanner.WaitForCallsAsync(2);

        // New scan completes first, then the stale one: the stale result must not win.
        scanner.Release(1);
        (await afterInvalidate).Should().BeEquivalentTo(Files("new"));
        scanner.Release(0);
        (await beforeInvalidate).Should().BeEquivalentTo(Files("old"));

        (await _cacheService.GetOrScanAsync()).Should().BeEquivalentTo(Files("new"));
        scanner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrScanAsync_ColdScanFailure_Propagates()
    {
        _mockScanner.Setup(s => s.ScanComposeFilesAsync()).ThrowsAsync(new InvalidOperationException("Scanner error"));

        Func<Task> act = () => _cacheService.GetOrScanAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Scanner error");
    }

    [Fact]
    public async Task GetOrScanAsync_BackgroundRescanFailure_KeepsStaleSnapshot()
    {
        _mockScanner.SetupSequence(s => s.ScanComposeFilesAsync())
            .ReturnsAsync(Files("a"))
            .ThrowsAsync(new IOException("disk error"))
            .ReturnsAsync(Files("a"));
        await _cacheService.GetOrScanAsync();
        ExpireTtl();

        (await _cacheService.GetOrScanAsync()).Should().BeEquivalentTo(Files("a"));
        await WaitForScanCountAsync(2);

        // Still served, and the next stale read retries the rescan.
        (await _cacheService.GetOrScanAsync()).Should().BeEquivalentTo(Files("a"));
        await WaitForScanCountAsync(3);
    }

    [Fact]
    public async Task BackgroundRescan_WithChangedFiles_BroadcastsComposeFilesChanged()
    {
        _mockScanner.SetupSequence(s => s.ScanComposeFilesAsync())
            .ReturnsAsync(Files("a"))
            .ReturnsAsync(Files("a", "b"));
        await _cacheService.GetOrScanAsync();
        SseClient client = AddFakeSseClient();
        ExpireTtl();

        await _cacheService.GetOrScanAsync();
        await WaitForScanCountAsync(2);

        ReadBody(client).Should().Contain($"event: {ComposeFileCacheService.FilesChangedEvent}");
    }

    [Fact]
    public async Task BackgroundRescan_WithUnchangedFiles_DoesNotBroadcast()
    {
        _mockScanner.SetupSequence(s => s.ScanComposeFilesAsync())
            .ReturnsAsync(Files("a", "b"))
            .ReturnsAsync(Files("b", "a")); // same content, different order
        await _cacheService.GetOrScanAsync();
        SseClient client = AddFakeSseClient();
        ExpireTtl();

        await _cacheService.GetOrScanAsync();
        await WaitForScanCountAsync(2);

        ReadBody(client).Should().BeEmpty();
    }

    [Fact]
    public async Task ColdScan_DoesNotBroadcast()
    {
        _mockScanner.Setup(s => s.ScanComposeFilesAsync()).ReturnsAsync(Files("a"));
        SseClient client = AddFakeSseClient();

        await _cacheService.GetOrScanAsync();
        _cacheService.Invalidate();
        await _cacheService.GetOrScanAsync();

        ReadBody(client).Should().BeEmpty();
    }

    [Fact]
    public async Task GetFreshAsync_FreshSnapshot_DoesNotScan()
    {
        _mockScanner.Setup(s => s.ScanComposeFilesAsync()).ReturnsAsync(Files("a"));
        await _cacheService.GetOrScanAsync();

        var result = await _cacheService.GetFreshAsync();

        result.Should().BeEquivalentTo(Files("a"));
        _mockScanner.Verify(s => s.ScanComposeFilesAsync(), Times.Once);
    }

    [Fact]
    public async Task GetFreshAsync_StaleSnapshot_AwaitsRescan()
    {
        _mockScanner.SetupSequence(s => s.ScanComposeFilesAsync())
            .ReturnsAsync(Files("a"))
            .ReturnsAsync(Files("a", "b"));
        await _cacheService.GetOrScanAsync();
        ExpireTtl();

        var result = await _cacheService.GetFreshAsync();

        result.Should().BeEquivalentTo(Files("a", "b"));
    }

    /// <summary>Minimal controllable clock (avoids a test-only package dependency).</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
