using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Services;
using Lighthouse.Services.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>
/// Tests the local-vs-remote digest comparison of <see cref="ImageDigestService"/>, in particular
/// local images that carry several RepoDigests (a tag re-published with a new index for the
/// same content), which must not be reported as outdated forever.
/// </summary>
public class ImageDigestServiceTests
{
    private const string Image = "traefik:v3.7.17";
    private const string DigestA = "sha256:92627e6a99f71b8e46634acea28130b9f14bec5d9887ad15161007e1b652afe1";
    private const string DigestB = "sha256:f4bb143d1135a579b630ed491e17ec50f6f7c9c6a30585ff3d385471d2a86f5a";
    private const string DigestNew = "sha256:0000000000000000000000000000000000000000000000000000000000000001";

    private readonly Mock<DockerCommandExecutorService> _executor =
        new(NullLogger<DockerCommandExecutorService>.Instance);
    private readonly Mock<IRegistryClientFactory> _clientFactory = new();
    private readonly Mock<IRegistryClient> _client = new();
    private readonly Mock<IRegistryRateLimitGate> _rateLimitGate = new();

    public ImageDigestServiceTests()
    {
        _executor.Setup(e => e.ExecuteAsync("docker", It.Is<string>(a => a.StartsWith("info")), It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, "amd64", string.Empty));

        _clientFactory.Setup(f => f.ParseImageReference(Image))
            .Returns(new ImageReference("docker.io", "library/traefik", "v3.7.17", null, "docker.io/library/traefik:v3.7.17"));
        _clientFactory.Setup(f => f.GetClient("docker.io")).Returns(_client.Object);

        TimeSpan remaining;
        _rateLimitGate.Setup(g => g.IsCoolingDown(out remaining)).Returns(false);
    }

    private ImageDigestService CreateService() => new(
        _executor.Object,
        _clientFactory.Object,
        _rateLimitGate.Object,
        Options.Create(new UpdateCheckOptions()),
        NullLogger<ImageDigestService>.Instance);

    private void SetupLocalRepoDigests(params string[] digests)
    {
        string entries = string.Join(",", digests.Select(d => $"\"traefik@{d}\""));
        string json = $$"""[{"Id":"sha256:d848","RepoDigests":[{{entries}}],"Architecture":"amd64","Created":"2026-11-05T19:11:35Z"}]""";
        _executor.Setup(e => e.ExecuteAsync("docker", $"image inspect {Image} --format json", It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, json, string.Empty));
    }

    private void SetupRemoteDigest(string digest)
    {
        _client.Setup(c => c.GetManifestDigestAsync("docker.io", "library/traefik", "v3.7.17", "amd64", It.IsAny<CancellationToken>()))
            .ReturnsAsync(digest);
        _client.Setup(c => c.GetManifestDigestAndCreatedAtAsync("docker.io", "library/traefik", "v3.7.17", "amd64", It.IsAny<CancellationToken>()))
            .ReturnsAsync((digest, (DateTime?)null));
    }

    [Fact]
    public async Task GetLocalDigestAsync_ReturnsAllRepoDigests()
    {
        SetupLocalRepoDigests(DigestA, DigestB);

        ImageDigestInfo info = await CreateService().GetLocalDigestAsync(Image);

        info.Digest.Should().Be(DigestA);
        info.RepoDigests.Should().Equal(DigestA, DigestB);
        info.IsLocalBuild.Should().BeFalse();
    }

    [Fact]
    public async Task GetLocalDigestAsync_NoRepoDigests_IsLocalBuild()
    {
        _executor.Setup(e => e.ExecuteAsync("docker", $"image inspect {Image} --format json", It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, """[{"Id":"sha256:d848","RepoDigests":[]}]""", string.Empty));

        ImageDigestInfo info = await CreateService().GetLocalDigestAsync(Image);

        info.Digest.Should().BeNull();
        info.IsLocalBuild.Should().BeTrue();
    }

    [Fact]
    public async Task CheckImageUpdateAsync_RemoteMatchesSecondRepoDigest_NoUpdate()
    {
        SetupLocalRepoDigests(DigestA, DigestB);
        SetupRemoteDigest(DigestB);

        ImageUpdateStatus status = await CreateService().CheckImageUpdateAsync(Image, "reverse-proxy");

        status.UpdateAvailable.Should().BeFalse();
        status.LocalDigest.Should().Be(DigestB);
        status.RemoteDigest.Should().Be(DigestB);
    }

    [Fact]
    public async Task CheckImageUpdateAsync_RemoteMatchesFirstRepoDigest_NoUpdate()
    {
        SetupLocalRepoDigests(DigestA, DigestB);
        SetupRemoteDigest(DigestA);

        ImageUpdateStatus status = await CreateService().CheckImageUpdateAsync(Image, "reverse-proxy");

        status.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CheckImageUpdateAsync_RemoteMatchesNoLocalDigest_UpdateAvailable()
    {
        SetupLocalRepoDigests(DigestA, DigestB);
        SetupRemoteDigest(DigestNew);

        ImageUpdateStatus status = await CreateService().CheckImageUpdateAsync(Image, "reverse-proxy");

        status.UpdateAvailable.Should().BeTrue();
        status.LocalDigest.Should().Be(DigestA);
        status.RemoteDigest.Should().Be(DigestNew);
    }

    [Fact]
    public void HasDigest_IsCaseInsensitiveAndChecksAllDigests()
    {
        ImageDigestInfo info = new(Image, DigestA, "amd64", null, false, false, null, new[] { DigestA, DigestB });

        info.HasDigest(DigestB.ToUpperInvariant()).Should().BeTrue();
        info.HasDigest(DigestNew).Should().BeFalse();
    }
}
