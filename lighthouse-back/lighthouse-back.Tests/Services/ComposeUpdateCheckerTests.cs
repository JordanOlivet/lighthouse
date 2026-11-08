using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>
/// Tests for <see cref="ComposeUpdateChecker"/> (extracted from ComposeUpdateService in PR6). Covers
/// the cache delegation and compose-file resolution. The digest-checking path pulls in the concrete
/// DockerService (live daemon), so it isn't unit-tested here.
/// </summary>
public class ComposeUpdateCheckerTests
{
    private readonly Mock<IImageDigestService> _imageDigest = new();
    private readonly Mock<IImageUpdateCacheService> _cache = new();
    private readonly Mock<IComposeFileCacheService> _fileCache = new();
    private readonly Mock<IProjectMatchingService> _projectMatching = new();
    private readonly Mock<IComposeEnvFileResolver> _envFiles = new();

    private ComposeUpdateChecker CreateChecker() => new(
        _imageDigest.Object,
        _cache.Object,
        _fileCache.Object,
        _projectMatching.Object,
        dockerService: null!,   // only used to enrich container states (best-effort, caught)
        sseManager: null!,      // only used by CheckAllProjectsUpdatesAsync's broadcast
        _envFiles.Object,
        Options.Create(new UpdateCheckOptions()),
        new NullLogger<ComposeUpdateChecker>());

    /// <summary>
    /// Writes <paramref name="composeYaml"/> (and an optional adjacent .env) to a temp project dir,
    /// registers it as the discovered file for <paramref name="project"/>, runs a check and returns
    /// the image references that were sent to the digest service.
    /// </summary>
    private async Task<List<string>> CheckedImagesAsync(string project, string composeYaml, string? dotEnv = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "lighthouse-checker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string composePath = Path.Combine(dir, project + ".yml");
            await File.WriteAllTextAsync(composePath, composeYaml);
            var envFiles = new List<string>();
            if (dotEnv != null)
            {
                string envPath = Path.Combine(dir, ".env");
                await File.WriteAllTextAsync(envPath, dotEnv);
                envFiles.Add(envPath);
            }

            _fileCache.Setup(f => f.GetOrScanAsync(It.IsAny<bool>()))
                .ReturnsAsync(new List<DiscoveredComposeFile> { DiscoveredFile(project, composePath) });
            _envFiles.Setup(e => e.GetEnvFilePathsAsync(dir, It.IsAny<CancellationToken>()))
                .ReturnsAsync(envFiles);

            var images = new List<string>();
            _imageDigest
                .Setup(d => d.CheckImageUpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string image, string service, CancellationToken _) =>
                {
                    lock (images) images.Add(image);
                    return new ImageUpdateStatus(image, service, "amd64", null, null, null, null, false, true, null, false, false, null);
                });

            await CreateChecker().CheckProjectUpdatesAsync(project);
            return images;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DiscoveredComposeFile DiscoveredFile(string project, string path) => new()
    {
        FilePath = path,
        ProjectName = project,
        DirectoryPath = "/c",
        IsValid = true
    };

    [Fact]
    public void GetGlobalUpdateStatus_DelegatesToCache()
    {
        var summaries = new List<ProjectUpdateSummary> { new("proj", 1, DateTime.UtcNow) };
        _cache.Setup(c => c.GetAllCachedSummaries()).Returns(summaries);

        CreateChecker().GetGlobalUpdateStatus().Should().BeSameAs(summaries);
    }

    [Fact]
    public void ClearCache_InvalidatesAll()
    {
        CreateChecker().ClearCache();
        _cache.Verify(c => c.InvalidateAll(), Times.Once);
    }

    [Fact]
    public async Task FindComposeFilePathAsync_DirectNameMatch_ReturnsPath()
    {
        _fileCache.Setup(f => f.GetOrScanAsync(false))
            .ReturnsAsync(new List<DiscoveredComposeFile> { DiscoveredFile("proj", "/c/proj/docker-compose.yml") });

        string? path = await CreateChecker().FindComposeFilePathAsync("proj");

        path.Should().Be("/c/proj/docker-compose.yml");
        _projectMatching.Verify(p => p.GetUnifiedProjectListAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task FindComposeFilePathAsync_FallsBackToProjectMatching()
    {
        _fileCache.Setup(f => f.GetOrScanAsync(false)).ReturnsAsync(new List<DiscoveredComposeFile>());
        _projectMatching.Setup(p => p.GetUnifiedProjectListAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<ComposeProjectDto>
            {
                new("proj", "/q", "running", new List<ComposeServiceDto>(), new List<string>(), null,
                    ComposeFilePath: "/q/docker-compose.yml", HasComposeFile: true)
            });

        string? path = await CreateChecker().FindComposeFilePathAsync("proj");

        path.Should().Be("/q/docker-compose.yml");
    }

    [Fact]
    public async Task FindComposeFilePathAsync_NotFound_ReturnsNull()
    {
        _fileCache.Setup(f => f.GetOrScanAsync(false)).ReturnsAsync(new List<DiscoveredComposeFile>());
        _projectMatching.Setup(p => p.GetUnifiedProjectListAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<ComposeProjectDto>());

        string? path = await CreateChecker().FindComposeFilePathAsync("missing");

        path.Should().BeNull();
    }

    [Fact]
    public async Task CheckProjectUpdates_ImageWithDefaults_IsInterpolatedBeforeCheck()
    {
        // Real case from production: the raw reference reached the registry client and threw
        // UriFormatException on every check.
        List<string> images = await CheckedImagesAsync("coder", """
            services:
              coder:
                image: ${CODER_REPO:-ghcr.io/coder/coder}:${CODER_VERSION:-latest}
            """);

        images.Should().Equal("ghcr.io/coder/coder:latest");
    }

    [Fact]
    public async Task CheckProjectUpdates_ImageVariableFromDotEnv_IsResolved()
    {
        List<string> images = await CheckedImagesAsync("app", """
            services:
              web:
                image: nginx:${LH_TEST_NGINX_TAG_9F2C}
            """, dotEnv: "LH_TEST_NGINX_TAG_9F2C=1.27-alpine\n");

        images.Should().Equal("nginx:1.27-alpine");
    }

    [Fact]
    public async Task CheckProjectUpdates_UnsetVariableWithoutDefault_SkipsImageOnly()
    {
        List<string> images = await CheckedImagesAsync("app", """
            services:
              web:
                image: nginx:${LH_TEST_UNSET_VAR_9F2C}
              db:
                image: postgres:16
            """);

        images.Should().Equal("postgres:16");
    }

    [Fact]
    public async Task CheckProjectUpdates_PlainImage_EnvFilesNotRead()
    {
        List<string> images = await CheckedImagesAsync("app", """
            services:
              web:
                image: nginx:1.27
            """);

        images.Should().Equal("nginx:1.27");
        _envFiles.Verify(e => e.GetEnvFilePathsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
