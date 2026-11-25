using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Lighthouse.Services.Oidc;

/// <summary>
/// Single-use, short-lived tickets that carry "who asked to link" across the redirect to the
/// SSO provider. Issued to an authenticated session (POST /api/auth/oidc/link), consumed by
/// GET /api/auth/oidc/login. In memory: Lighthouse runs as a single instance.
/// </summary>
public class OidcLinkTicketStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const string KeyPrefix = "oidc-link-ticket:";

    private readonly IMemoryCache _cache;

    public OidcLinkTicketStore(IMemoryCache cache) => _cache = cache;

    public string Issue(int userId)
    {
        var ticket = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _cache.Set(KeyPrefix + ticket, userId, Lifetime);
        return ticket;
    }

    public int? Consume(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_cache.TryGetValue(KeyPrefix + ticket, out int userId))
            return null;
        _cache.Remove(KeyPrefix + ticket);
        return userId;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
