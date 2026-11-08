using FluentAssertions;
using System.Net.Http.Headers;
using Lighthouse.Configuration;
using Lighthouse.Services;
using Lighthouse.Services.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lighthouse.Tests.Services.Registry;

/// <summary>
/// Characterization tests pinning the externally observable behaviour of
/// <see cref="GenericOciRegistryClient"/> (the fallback client; targets the registry host it is
/// given and authenticates via the WWW-Authenticate challenge).
/// </summary>
public class GenericOciRegistryClientTests
{
    private const string Registry = "myreg.io";
    private const string Repository = "owner/repo";

    private static GenericOciRegistryClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateClient(responder, out _);

    private static GenericOciRegistryClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out FakeHttpMessageHandler handler,
        IRegistryCredentialService? credentials = null)
    {
        handler = new FakeHttpMessageHandler(responder);
        return new GenericOciRegistryClient(
            handler.CreateClient(),
            Options.Create(new UpdateCheckOptions { TimeoutSeconds = 30 }),
            credentials ?? RegistryTestCredentials.None(),
            new NullLogger<GenericOciRegistryClient>());
    }

    /// <summary>
    /// Registry that requires a token for everything (separate auth host, like GitLab): records the
    /// Authorization seen by the token endpoint and serves a digest to bearer-authenticated HEADs.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> TokenOnlyResponder(
        List<AuthenticationHeaderValue?> tokenRequestAuth)
        => req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.StartsWith("https://auth.myreg.io/token"))
            {
                tokenRequestAuth.Add(req.Headers.Authorization);
                return RegistryResponses.Json("{\"token\":\"tok\"}");
            }
            if (req.Headers.Authorization?.Scheme == "Bearer" && req.Method == HttpMethod.Head)
                return RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:genauthed");
            return RegistryResponses.Unauthorized("https://auth.myreg.io/token", "myreg.io", "repository:owner/repo:pull");
        };

    [Fact]
    public void CanHandle_AlwaysTrue()
    {
        GenericOciRegistryClient client = CreateClient(_ => RegistryResponses.NotFound());
        client.CanHandle("anything.example.com").Should().BeTrue();
    }

    [Fact]
    public async Task GetManifestDigestAsync_AnonymousHead_ReturnsDigest()
    {
        GenericOciRegistryClient client = CreateClient(req =>
            req.Method == HttpMethod.Head
                ? RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:generichead")
                : RegistryResponses.NotFound());

        string? digest = await client.GetManifestDigestAsync(Registry, Repository, "latest", "amd64");

        digest.Should().Be("sha256:generichead");
    }

    [Fact]
    public async Task GetManifestDigestAsync_TargetsProvidedRegistryHost()
    {
        // Regression: the client used to re-derive the registry from the repository string and
        // ended up querying docker.io for images hosted on other registries (e.g. lscr.io).
        GenericOciRegistryClient client = CreateClient(req =>
            req.Method == HttpMethod.Head
                ? RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:hosted")
                : RegistryResponses.NotFound(),
            out FakeHttpMessageHandler handler);

        string? digest = await client.GetManifestDigestAsync("lscr.io", "linuxserver/radarr", "latest", "amd64");

        digest.Should().Be("sha256:hosted");
        handler.Requests.Should().OnlyContain(r =>
            r.Url.StartsWith("https://lscr.io/v2/linuxserver/radarr/"));
    }

    [Fact]
    public async Task GetManifestDigestAsync_AnonymousFailsThenTokenChallenge_ReturnsDigest()
    {
        GenericOciRegistryClient client = CreateClient(req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/token"))
                return RegistryResponses.Json("{\"token\":\"tok\"}");
            // Anonymous HEAD fails → null, triggering the token challenge.
            if (req.Method == HttpMethod.Head && req.Headers.Authorization == null)
                return RegistryResponses.NotFound();
            // Authenticated HEAD succeeds.
            if (req.Method == HttpMethod.Head)
                return RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:genauthed");
            // GET manifest with no auth → 401 challenge (used by the token probe).
            if (req.Method == HttpMethod.Get && req.Headers.Authorization == null)
                return RegistryResponses.Unauthorized("https://myreg.io/token", "myreg.io", "repository:owner/repo:pull");
            return RegistryResponses.NotFound();
        });

        string? digest = await client.GetManifestDigestAsync(Registry, Repository, "latest", "amd64");

        digest.Should().Be("sha256:genauthed");
    }

    [Fact]
    public async Task GetManifestDigestAsync_TokenProbeUsesRequestedTag()
    {
        // Regression: the token probe used a hardcoded "manifests/latest" URL; registries that
        // validate the tag before issuing the challenge would fail for repos without that tag.
        GenericOciRegistryClient client = CreateClient(req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/token"))
                return RegistryResponses.Json("{\"token\":\"tok\"}");
            if (url.Contains("/manifests/latest"))
                return RegistryResponses.NotFound();
            if (req.Headers.Authorization == null)
                return RegistryResponses.Unauthorized("https://myreg.io/token", "myreg.io", "repository:owner/repo:pull");
            if (req.Method == HttpMethod.Head)
                return RegistryResponses.Head(System.Net.HttpStatusCode.OK, "sha256:taggeddigest");
            return RegistryResponses.NotFound();
        }, out FakeHttpMessageHandler handler);

        string? digest = await client.GetManifestDigestAsync(Registry, Repository, "4.3.2", "amd64");

        digest.Should().Be("sha256:taggeddigest");
        handler.Requests.Should().NotContain(r => r.Url.Contains("/manifests/latest"));
    }

    [Fact]
    public async Task GetManifestDigestAndCreatedAtAsync_SingleArchAnonymous_ReturnsDigestAndCreatedAt()
    {
        GenericOciRegistryClient client = CreateClient(req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/blobs/"))
                return RegistryResponses.Json(RegistryResponses.ConfigBlobBody("2026-09-10T11:12:13Z"));
            return RegistryResponses.Manifest(
                RegistryResponses.ManifestContentType,
                RegistryResponses.ManifestBody("sha256:cfg"),
                dockerContentDigest: "sha256:gensingle");
        });

        (string? digest, DateTime? createdAt) =
            await client.GetManifestDigestAndCreatedAtAsync(Registry, Repository, "latest", "amd64");

        digest.Should().Be("sha256:gensingle");
        createdAt.Should().Be(new DateTime(2026, 9, 10, 11, 12, 13, DateTimeKind.Utc).ToLocalTime());
    }

    [Fact]
    public async Task GetManifestDigestAsync_DockerConfigCredentialsForHost_SentAsBasicToTokenEndpoint()
    {
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        IRegistryCredentialService credentials = RegistryTestCredentials.For(
            new Dictionary<string, (string, string)> { [Registry] = ("alice", "s3cret") });
        GenericOciRegistryClient client = CreateClient(TokenOnlyResponder(tokenAuth), out _, credentials);

        string? digest = await client.GetManifestDigestAsync(Registry, Repository, "latest", "amd64");

        digest.Should().Be("sha256:genauthed");
        tokenAuth.Should().ContainSingle();
        tokenAuth[0]!.Scheme.Should().Be("Basic");
        tokenAuth[0]!.Parameter.Should().Be(RegistryTestCredentials.BasicValue("alice", "s3cret"));
    }

    [Fact]
    public async Task GetManifestDigestAsync_CredentialsForOtherHostOnly_RequestsAnonymousToken()
    {
        var tokenAuth = new List<AuthenticationHeaderValue?>();
        IRegistryCredentialService credentials = RegistryTestCredentials.For(
            new Dictionary<string, (string, string)> { ["ghcr.io"] = ("alice", "ghp_secret") });
        GenericOciRegistryClient client = CreateClient(TokenOnlyResponder(tokenAuth), out _, credentials);

        string? digest = await client.GetManifestDigestAsync(Registry, Repository, "latest", "amd64");

        digest.Should().Be("sha256:genauthed");
        tokenAuth.Should().ContainSingle().Which.Should().BeNull();
    }
}
