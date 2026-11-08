using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.Services;
using Lighthouse.Services.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace Lighthouse.Tests.Services.Registry;

/// <summary>
/// End-to-end checks of the registry clients against the real public registries. Skipped unless
/// RUN_REGISTRY_INTEGRATION=1 (see <see cref="IntegrationFactAttribute"/>). They validate that the
/// real auth + manifest-parsing flow resolves a digest (and creation date) for a known public image
/// after the RegistryClientBase refactor — i.e. the part the mocked characterization tests cannot cover.
/// </summary>
[Trait("Category", "RegistryIntegration")]
public class RegistryClientsIntegrationTests
{
    private const string Architecture = "amd64";

    private readonly ITestOutputHelper _output;

    public RegistryClientsIntegrationTests(ITestOutputHelper output) => _output = output;

    private static IOptions<UpdateCheckOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new UpdateCheckOptions { TimeoutSeconds = 30 });

    private static DockerHubRegistryClient DockerHub()
    {
        // No stored Docker Hub credentials → anonymous token (enough for public images).
        var credentials = new Mock<IRegistryCredentialService>();
        credentials.Setup(c => c.GetRawCredentialsAsync(It.IsAny<string>()))
            .ReturnsAsync(((string Username, string Password)?)null);
        return new DockerHubRegistryClient(new HttpClient(), Options(), credentials.Object,
            new NullLogger<DockerHubRegistryClient>());
    }

    private static GhcrRegistryClient Ghcr()
        => new(new HttpClient(), Options(), new ConfigurationBuilder().Build(),
            RegistryTestCredentials.None(), new NullLogger<GhcrRegistryClient>());

    private static GenericOciRegistryClient Generic()
        => new(new HttpClient(), Options(), RegistryTestCredentials.None(),
            new NullLogger<GenericOciRegistryClient>());

    private async Task AssertResolvesAsync(IRegistryClient client, string registry, string repository, string tag)
    {
        string? headDigest = await client.GetManifestDigestAsync(registry, repository, tag, Architecture);
        (string? getDigest, DateTime? createdAt) =
            await client.GetManifestDigestAndCreatedAtAsync(registry, repository, tag, Architecture);

        _output.WriteLine($"{registry}/{repository}:{tag}");
        _output.WriteLine($"  HEAD digest : {headDigest}");
        _output.WriteLine($"  GET  digest : {getDigest}");
        _output.WriteLine($"  created at  : {createdAt:O}");

        headDigest.Should().StartWith("sha256:");
        getDigest.Should().StartWith("sha256:");
        // HEAD and GET resolve the same content-addressable digest for the same tag.
        headDigest.Should().Be(getDigest);
        createdAt.Should().NotBeNull();
    }

    [IntegrationFact]
    public async Task DockerHub_ResolvesDigestForLibraryNginx()
        => await AssertResolvesAsync(DockerHub(), "docker.io", "library/nginx", "latest");

    [IntegrationFact]
    public async Task Ghcr_ResolvesDigestForPublicImage()
        => await AssertResolvesAsync(Ghcr(), "ghcr.io", "home-assistant/home-assistant", "stable");

    [IntegrationFact]
    public async Task GenericOci_ResolvesDigestForMcr()
        => await AssertResolvesAsync(Generic(), "mcr.microsoft.com", "dotnet/aspnet", "10.0-noble");

    [IntegrationFact]
    public async Task GenericOci_ResolvesDigestForLscr()
        => await AssertResolvesAsync(Generic(), "lscr.io", "linuxserver/radarr", "latest");
}
