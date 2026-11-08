using System.Net;
using System.Net.Http.Headers;
using Lighthouse.Services;
using Moq;

namespace Lighthouse.Tests.Services.Registry;

/// <summary>
/// Programmable <see cref="HttpMessageHandler"/> for registry-client tests. Routes each request to a
/// caller-supplied responder and records the requests it saw, so tests can assert on the sequence of
/// calls (token endpoint, HEAD/GET manifest, config blob, ...).
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<(HttpMethod Method, string Url)> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri!.ToString()));
        return Task.FromResult(_responder(request));
    }

    public HttpClient CreateClient() => new(this);
}

/// <summary>
/// Helpers to build the registry HTTP responses used by the characterization tests.
/// </summary>
public static class RegistryResponses
{
    public const string ManifestListContentType = "application/vnd.docker.distribution.manifest.list.v2+json";
    public const string ManifestContentType = "application/vnd.docker.distribution.manifest.v2+json";

    /// <summary>Manifest response carrying a Docker-Content-Digest header and a body.</summary>
    public static HttpResponseMessage Manifest(string contentType, string body, string? dockerContentDigest = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType.Split(';')[0]);
        if (dockerContentDigest != null)
        {
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", dockerContentDigest);
        }
        return response;
    }

    /// <summary>HEAD response: status + optional Docker-Content-Digest header, no body.</summary>
    public static HttpResponseMessage Head(HttpStatusCode status, string? dockerContentDigest = null)
    {
        var response = new HttpResponseMessage(status);
        if (dockerContentDigest != null)
        {
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", dockerContentDigest);
        }
        return response;
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    public static HttpResponseMessage TooManyRequests()
        => new(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") };

    /// <summary>401 with a Bearer WWW-Authenticate challenge pointing at a token realm.</summary>
    public static HttpResponseMessage Unauthorized(string realm, string service, string scope)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation(
            "WWW-Authenticate", $"Bearer realm=\"{realm}\",service=\"{service}\",scope=\"{scope}\"");
        return response;
    }

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    /// <summary>A docker manifest list (image index) body for the given arch → manifest digest.</summary>
    public static string ManifestListBody(string architecture, string archManifestDigest)
        => $$"""
        {
          "manifests": [
            { "platform": { "architecture": "{{architecture}}", "os": "linux" }, "digest": "{{archManifestDigest}}" }
          ]
        }
        """;

    /// <summary>A single image manifest body referencing a config blob digest.</summary>
    public static string ManifestBody(string configDigest)
        => $$"""
        { "config": { "digest": "{{configDigest}}" } }
        """;

    /// <summary>A config blob body with a created timestamp.</summary>
    public static string ConfigBlobBody(string createdIso)
        => $$"""
        { "created": "{{createdIso}}" }
        """;
}

/// <summary>
/// <see cref="IRegistryCredentialService"/> stubs for the registry-client tests.
/// </summary>
public static class RegistryTestCredentials
{
    /// <summary>No credentials configured for any registry (anonymous tokens).</summary>
    public static IRegistryCredentialService None() => For(new Dictionary<string, (string, string)>());

    /// <summary>Credentials configured for the given registry keys only.</summary>
    public static IRegistryCredentialService For(IDictionary<string, (string Username, string Password)> byRegistry)
    {
        var mock = new Mock<IRegistryCredentialService>();
        mock.Setup(c => c.GetRawCredentialsAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => byRegistry.TryGetValue(key, out var creds)
                ? creds
                : ((string Username, string Password)?)null);
        return mock.Object;
    }

    /// <summary>The <c>Basic</c> header value expected for a username / password pair.</summary>
    public static string BasicValue(string username, string password)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
}
