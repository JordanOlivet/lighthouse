using System.Text.RegularExpressions;
using Lighthouse.Configuration;
using Lighthouse.Data;
using Lighthouse.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lighthouse.Services.Oidc;

public record OidcResolveResult(User? User, string? ErrorCode)
{
    public bool Success => User != null;

    public static OidcResolveResult Ok(User user) => new(user, null);
    public static OidcResolveResult Fail(string errorCode) => new(null, errorCode);
}

public interface IOidcUserResolver
{
    Task<OidcResolveResult> ResolveAsync(OidcIdentity identity, CancellationToken ct = default);

    /// <summary>
    /// Links the provider identity to an account the caller already proved it owns (open session).
    /// No email comparison: the person authenticated on both sides.
    /// </summary>
    Task<OidcResolveResult> LinkAsync(int userId, OidcIdentity identity, CancellationToken ct = default);
}

/// <summary>
/// Maps an OIDC identity to a Lighthouse account:
/// 1. account already linked to (iss, sub);
/// 2. otherwise first login: account with the same (verified) email, which gets linked —
///    except admin accounts, which are only linked explicitly (<see cref="LinkAsync"/>) so that
///    a provider account carrying an admin's email cannot take the admin account over;
/// 3. otherwise create one when AutoProvision is on.
/// An account already linked to a different subject is never re-linked silently.
/// </summary>
public partial class OidcUserResolver : IOidcUserResolver
{
    private const string ProvisionedRoleName = "user";
    private const string AdminRoleName = "admin";

    private readonly AppDbContext _context;
    private readonly OidcOptions _options;
    private readonly ILogger<OidcUserResolver> _logger;

    public OidcUserResolver(AppDbContext context, IOptions<OidcOptions> options, ILogger<OidcUserResolver> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OidcResolveResult> ResolveAsync(OidcIdentity identity, CancellationToken ct = default)
    {
        var linked = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.OidcIssuer == identity.Issuer && u.OidcSubject == identity.Subject, ct);

        if (linked != null)
        {
            return linked.IsEnabled
                ? OidcResolveResult.Ok(linked)
                : Refuse(OidcErrorCodes.AccountDisabled, identity);
        }

        if (identity.Email == null)
            return Refuse(OidcErrorCodes.EmailMissing, identity);

        if (_options.RequireVerifiedEmail && !identity.EmailVerified)
            return Refuse(OidcErrorCodes.EmailNotVerified, identity);

        var email = identity.Email.ToLowerInvariant();
        var matches = await _context.Users
            .Include(u => u.Role)
            .Where(u => u.Email != null && u.Email.ToLower() == email)
            .Take(2)
            .ToListAsync(ct);

        // Emails are unique case-sensitively only: never pick one of several accounts arbitrarily.
        if (matches.Count > 1)
        {
            _logger.LogWarning("OIDC email {Email} matches several accounts; refusing to link", identity.Email);
            return Refuse(OidcErrorCodes.AccountNotFound, identity);
        }

        var byEmail = matches.SingleOrDefault();
        if (byEmail != null)
        {
            if (!byEmail.IsEnabled)
                return Refuse(OidcErrorCodes.AccountDisabled, identity);

            if (byEmail.OidcSubject != null)
                return Refuse(OidcErrorCodes.SubjectMismatch, identity);

            if (byEmail.Role?.Name == AdminRoleName)
                return Refuse(OidcErrorCodes.AdminLinkRequired, identity);

            byEmail.OidcIssuer = identity.Issuer;
            byEmail.OidcSubject = identity.Subject;
            byEmail.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Linked user {Username} (id {UserId}) to OIDC subject {Subject} by email {Email}",
                byEmail.Username, byEmail.Id, identity.Subject, identity.Email);
            return OidcResolveResult.Ok(byEmail);
        }

        if (!_options.AutoProvision)
            return Refuse(OidcErrorCodes.AccountNotFound, identity);

        return OidcResolveResult.Ok(await ProvisionAsync(identity, ct));
    }

    public async Task<OidcResolveResult> LinkAsync(int userId, OidcIdentity identity, CancellationToken ct = default)
    {
        var user = await _context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null)
            return Refuse(OidcErrorCodes.AccountNotFound, identity);
        if (!user.IsEnabled)
            return Refuse(OidcErrorCodes.AccountDisabled, identity);

        if (user.OidcSubject != null)
        {
            return user.OidcIssuer == identity.Issuer && user.OidcSubject == identity.Subject
                ? OidcResolveResult.Ok(user)
                : Refuse(OidcErrorCodes.SubjectMismatch, identity);
        }

        var takenBy = await _context.Users.AnyAsync(
            u => u.Id != userId && u.OidcIssuer == identity.Issuer && u.OidcSubject == identity.Subject, ct);
        if (takenBy)
            return Refuse(OidcErrorCodes.IdentityInUse, identity);

        user.OidcIssuer = identity.Issuer;
        user.OidcSubject = identity.Subject;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Linked user {Username} (id {UserId}) to OIDC subject {Subject} from an open session",
            user.Username, user.Id, identity.Subject);
        return OidcResolveResult.Ok(user);
    }

    private async Task<User> ProvisionAsync(OidcIdentity identity, CancellationToken ct)
    {
        var role = await _context.Roles.FirstAsync(r => r.Name == ProvisionedRoleName, ct);
        var username = await FindFreeUsernameAsync(BaseUsername(identity), ct);

        var user = new User
        {
            Username = username,
            Email = identity.Email,
            PasswordHash = null,
            RoleId = role.Id,
            IsEnabled = true,
            MustChangePassword = false,
            MustAddEmail = false,
            OidcIssuer = identity.Issuer,
            OidcSubject = identity.Subject,
            CreatedAt = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync(ct);
        user.Role = role;

        _logger.LogInformation("Provisioned user {Username} (id {UserId}) for OIDC subject {Subject}",
            user.Username, user.Id, identity.Subject);
        return user;
    }

    /// <summary>
    /// preferred_username, else the email local part, reduced to the characters local
    /// usernames allow ([a-zA-Z0-9_-], 3-100 chars).
    /// </summary>
    private static string BaseUsername(OidcIdentity identity)
    {
        var raw = !string.IsNullOrWhiteSpace(identity.PreferredUsername)
            ? identity.PreferredUsername
            : identity.Email!.Split('@')[0];

        var cleaned = InvalidUsernameChars().Replace(raw.Trim(), "-").Trim('-');
        if (cleaned.Length > 90) cleaned = cleaned[..90];
        return cleaned.Length >= 3 ? cleaned : "user";
    }

    private async Task<string> FindFreeUsernameAsync(string baseName, CancellationToken ct)
    {
        var candidate = baseName;
        for (var suffix = 2; await _context.Users.AnyAsync(u => u.Username == candidate, ct); suffix++)
            candidate = baseName + suffix;
        return candidate;
    }

    private OidcResolveResult Refuse(string code, OidcIdentity identity)
    {
        _logger.LogWarning("OIDC login refused ({Code}) for subject {Subject}, email {Email}",
            code, identity.Subject, identity.Email);
        return OidcResolveResult.Fail(code);
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]+")]
    private static partial Regex InvalidUsernameChars();
}
