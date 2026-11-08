using FluentAssertions;
using System.Net.Http.Headers;
using Lighthouse.Configuration;
using Lighthouse.Services;
using Lighthouse.Services.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lighthouse.Tests.Services.Registry;

/// <summary>
/// Characterization tests pinning the externally observable behaviour of
/// <see cref="GhcrRegistryClient"/>, plus the credentials it sends to GHCR's token endpoint.
/// </summary>
public class GhcrRegistryClientTests
{
    private const string GhcrRealm = "https://ghcr.io/token";

    private static GhcrRegistryClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateClient(responder, out _);

    private static GhcrRegistryClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out FakeHttpMessageHandler handler,
        IRegistryCredentialService? credentials = null,
        IDictionary<string, string?>? settings = null)
    {
        handler = new FakeHttpMessageHandler(responder);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        return new GhcrRegistryClient(
            handler.CreateClient(),
            Options.Create(new UpdateCheckOptions { TimeoutSeconds = 30 }),
            configuration,
            credentials ?? RegistryTestCredentials.None(),
            new NullLogger<GhcrRegistryClient>());
    }

    /// <summary>
    /// GHCR-like responder: unauthenticated manifest requests get a 401 challenge pointing at
    /// <paramref name="realm"/>; the token endpoint records the Authorization it received and returns
    /// a token; bearer-authenticated requests return a digest.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> ChallengeResponder(
        List<AuthenticationHeaderValue?> tokenRequestAuth,
        string realm = GhcrRealm,
        Func<AuthenticationHeaderValue?, HttpResponseMessage>? tokenEndpoint = null)
        => req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.StartsWith(realm))
            {
                tokenRequestAuth.Add(req.Headers.Authorization);
                return tokenEndpoint?.Invoke(req.Headers.Authorization)
                    ?? RegistryResponses.Json("{\"token\":\"registry-token\"}");
            }
            if (req.Headers.Authorization?.Scheme == "Bearer")
                return RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:authed");
            return RegistryResponses.Unauthorized(realm, "ghcr.io", "repository:owner/repo:pull");
        };

    [Fact]
    public void CanHandle_OnlyGhcr()
    {
        GhcrRegistryClient client = CreateClient(_ => RegistryResponses.NotFound());
        client.CanHandle("ghcr.io").Should().BeTrue();
        client.CanHandle("docker.io").Should().BeFalse();
    }

    [Fact]
    public async Task GetManifestDigestAsync_AnonymousHead_ReturnsDigest()
    {
        GhcrRegistryClient client = CreateClient(req =>
            req.Method == HttpMethod.Head
                ? RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:ghcrhead")
                : RegistryResponses.NotFound());

        string? digest = await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        digest.Should().Be("sha256:ghcrhead");
    }

    [Fact]
    public async Task GetManifestDigestAsync_HeadUnauthorizedThenToken_ReturnsDigest()
    {
        GhcrRegistryClient client = CreateClient(req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/token"))
                return RegistryResponses.Json("{\"token\":\"tok\"}");
            if (req.Method == HttpMethod.Head && req.Headers.Authorization != null)
                return RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:authedhead");
            if (req.Method == HttpMethod.Head)
                return RegistryResponses.Unauthorized("https://ghcr.io/token", "ghcr.io", "repository:owner/repo:pull");
            return RegistryResponses.NotFound();
        });

        string? digest = await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        digest.Should().Be("sha256:authedhead");
    }

    [Fact]
    public async Task GetManifestDigestAsync_RateLimited_Throws()
    {
        GhcrRegistryClient client = CreateClient(_ => RegistryResponses.TooManyRequests());

        Func<Task> act = () => client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        await act.Should().ThrowAsync<RegistryRateLimitException>();
    }

    [Fact]
    public async Task GetManifestDigestAndCreatedAtAsync_SingleArch_ReturnsDigestAndCreatedAt()
    {
        GhcrRegistryClient client = CreateClient(req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/blobs/"))
                return RegistryResponses.Json(RegistryResponses.ConfigBlobBody("2026-07-08T09:10:11Z"));
            return RegistryResponses.Manifest(
                RegistryResponses.ManifestContentType,
                RegistryResponses.ManifestBody("sha256:cfg"),
                dockerContentDigest: "sha256:ghcrsingle");
        });

        (string? digest, DateTime? createdAt) =
            await client.GetManifestDigestAndCreatedAtAsync("ghcr.io", "owner/repo", "latest", "amd64");

        digest.Should().Be("sha256:ghcrsingle");
        createdAt.Should().Be(new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc).ToLocalTime());
    }

    [Fact]
    public async Task GetManifestDigestAsync_DockerConfigCredentials_SentAsBasicToTokenEndpoint()
    {
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        IRegistryCredentialService credentials = RegistryTestCredentials.For(
            new Dictionary<string, (string, string)> { ["ghcr.io"] = ("someone", "ghp_secret") });
        GhcrRegistryClient client = CreateClient(ChallengeResponder(tokenAuth), out FakeHttpMessageHandler handler, credentials);

        string? digest = await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        digest.Should().Be("sha256:authed");
        tokenAuth.Should().ContainSingle();
        tokenAuth[0]!.Scheme.Should().Be("Basic");
        tokenAuth[0]!.Parameter.Should().Be(RegistryTestCredentials.BasicValue("someone", "ghp_secret"));
        handler.Requests.Should().OnlyContain(r => r.Url.StartsWith("https://ghcr.io/"));
    }

    [Fact]
    public async Task GetManifestDigestAsync_ConfiguredGitHubToken_UsedWhenNoDockerConfigCredentials()
    {
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        GhcrRegistryClient client = CreateClient(
            ChallengeResponder(tokenAuth), out _,
            settings: new Dictionary<string, string?> { ["GitHub:Token"] = "ghp_fromenv" });

        string? digest = await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        digest.Should().Be("sha256:authed");
        tokenAuth.Should().ContainSingle();
        tokenAuth[0]!.Scheme.Should().Be("Basic");
        tokenAuth[0]!.Parameter.Should().Be(RegistryTestCredentials.BasicValue("token", "ghp_fromenv"));
    }

    [Fact]
    public async Task GetManifestDigestAsync_DockerConfigCredentials_TakePrecedenceOverGitHubToken()
    {
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        IRegistryCredentialService credentials = RegistryTestCredentials.For(
            new Dictionary<string, (string, string)> { ["ghcr.io"] = ("user", "ghp_config") });
        GhcrRegistryClient client = CreateClient(
            ChallengeResponder(tokenAuth), out _, credentials,
            new Dictionary<string, string?> { ["GitHub:Token"] = "ghp_fromenv" });

        await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        tokenAuth[0]!.Parameter.Should().Be(RegistryTestCredentials.BasicValue("user", "ghp_config"));
    }

    [Fact]
    public async Task GetManifestDigestAsync_NoCredentials_RequestsAnonymousToken()
    {
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        GhcrRegistryClient client = CreateClient(ChallengeResponder(tokenAuth), out _);

        string? digest = await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        digest.Should().Be("sha256:authed");
        tokenAuth.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task GetManifestDigestAsync_RejectedCredentials_FallBackToAnonymousToken()
    {
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        IRegistryCredentialService credentials = RegistryTestCredentials.For(
            new Dictionary<string, (string, string)> { ["ghcr.io"] = ("user", "ghp_expired") });
        GhcrRegistryClient client = CreateClient(
            ChallengeResponder(tokenAuth, tokenEndpoint: auth => auth == null
                ? RegistryResponses.Json("{\"token\":\"anon\"}")
                : new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)),
            out _, credentials);

        string? digest = await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        digest.Should().Be("sha256:authed");
        tokenAuth.Should().HaveCount(2);
        tokenAuth[0]!.Scheme.Should().Be("Basic");
        tokenAuth[1].Should().BeNull();
    }

    [Fact]
    public async Task GetManifestDigestAsync_ForeignRealm_NeverReceivesCredentials()
    {
        // Defence in depth: GitHub credentials are only handed to ghcr.io's own token endpoint.
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        IRegistryCredentialService credentials = RegistryTestCredentials.For(
            new Dictionary<string, (string, string)> { ["ghcr.io"] = ("user", "ghp_secret") });
        GhcrRegistryClient client = CreateClient(
            ChallengeResponder(tokenAuth, realm: "https://evil.example.com/token"), out _, credentials);

        await client.GetManifestDigestAsync("ghcr.io", "owner/repo", "latest", "amd64");

        tokenAuth.Should().ContainSingle().Which.Should().BeNull();
    }
}
