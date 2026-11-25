using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Lighthouse.Data;
using Lighthouse.Models;
using Lighthouse.DTOs;
using Lighthouse.Services.Security;
using DockerComposeManager.Services.Security;

namespace Lighthouse.Services;

public class AuthService
{
    private readonly AppDbContext _context;
    private readonly JwtTokenService _jwtService;
    private readonly IConfiguration _configuration;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext context,
        JwtTokenService jwtService,
        IConfiguration configuration,
        IPasswordHasher passwordHasher,
        IMemoryCache cache,
        ILogger<AuthService> logger)
    {
        _context = context;
        _jwtService = jwtService;
        _configuration = configuration;
        _passwordHasher = passwordHasher;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// SHA-256 hash (hex) of a refresh token. Only the hash is persisted; the raw token
    /// stays in the client's HttpOnly cookie.
    /// </summary>
    private static string HashToken(string rawToken)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hash);
    }

    public async Task<(bool Success, LoginResponse? Response, DateTime? RefreshExpiresAt, string? Error)> LoginAsync(LoginRequest request, string ipAddress, string deviceInfo)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user == null || !user.IsEnabled)
        {
            return (false, null, null, "Invalid credentials");
        }

        // OIDC-only accounts have no local password: same generic error, no hint.
        if (user.PasswordHash == null || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return (false, null, null, "Invalid credentials");
        }

        // Generate tokens
        var accessToken = _jwtService.GenerateAccessToken(user);
        var refreshToken = _jwtService.GenerateRefreshToken();

        // Calculate refresh token expiration based on RememberMe
        var refreshExpirationDays = request.RememberMe
            ? int.Parse(_configuration["Jwt:RefreshExpirationDaysExtended"] ?? "30")
            : int.Parse(_configuration["Jwt:RefreshExpirationDays"] ?? "1");
        var expiresAt = DateTime.UtcNow.AddDays(refreshExpirationDays);

        // New login starts a fresh refresh-token family.
        var session = new Session
        {
            UserId = user.Id,
            RefreshToken = HashToken(refreshToken),
            FamilyId = Guid.NewGuid().ToString("N"),
            IsUsed = false,
            DeviceInfo = deviceInfo,
            IpAddress = ipAddress,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            LastUsedAt = DateTime.UtcNow
        };

        _context.Sessions.Add(session);

        // Update last login
        user.LastLoginAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        var response = new LoginResponse(
            accessToken,
            refreshToken,
            user.Username,
            user.Role?.Name ?? "user",
            user.MustChangePassword,
            user.MustAddEmail
        );

        return (true, response, expiresAt, null);
    }

    public async Task<(bool Success, LoginResponse? Response, DateTime? RefreshExpiresAt, string? Error)> RefreshTokenAsync(string refreshToken, string ipAddress)
    {
        var tokenHash = HashToken(refreshToken);

        var session = await _context.Sessions
            .Include(s => s.User)
                .ThenInclude(u => u.Role)
            .FirstOrDefaultAsync(s => s.RefreshToken == tokenHash);

        if (session == null)
        {
            return (false, null, null, "Invalid or expired refresh token");
        }

        // Reuse detection: a token that was already rotated (or belongs to a revoked
        // family) is being presented again -> likely theft. Revoke the whole family so
        // neither the attacker nor the victim can keep using it.
        if (session.IsUsed || session.RevokedAt != null)
        {
            _logger.LogWarning(
                "Refresh token reuse detected for user {UserId}, family {FamilyId}. Revoking family.",
                session.UserId, session.FamilyId);
            await RevokeFamilyAsync(session.UserId, session.FamilyId);
            return (false, null, null, "Invalid or expired refresh token");
        }

        if (session.ExpiresAt < DateTime.UtcNow || !session.User.IsEnabled)
        {
            return (false, null, null, "Invalid or expired refresh token");
        }

        // Rotate: consume the current token and issue a new one in the same family.
        var accessToken = _jwtService.GenerateAccessToken(session.User);
        var newRefreshToken = _jwtService.GenerateRefreshToken();

        session.IsUsed = true;
        session.LastUsedAt = DateTime.UtcNow;

        var newSession = new Session
        {
            UserId = session.UserId,
            RefreshToken = HashToken(newRefreshToken),
            FamilyId = session.FamilyId,
            IsUsed = false,
            DeviceInfo = session.DeviceInfo,
            IpAddress = ipAddress,
            CreatedAt = DateTime.UtcNow,
            // Rotation keeps the original family expiry (no sliding window here).
            ExpiresAt = session.ExpiresAt,
            LastUsedAt = DateTime.UtcNow
        };
        _context.Sessions.Add(newSession);

        await _context.SaveChangesAsync();

        var response = new LoginResponse(
            accessToken,
            newRefreshToken,
            session.User.Username,
            session.User.Role?.Name ?? "user",
            session.User.MustChangePassword,
            session.User.MustAddEmail
        );

        return (true, response, session.ExpiresAt, null);
    }

    public async Task<bool> LogoutAsync(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);
        var session = await _context.Sessions.FirstOrDefaultAsync(s => s.RefreshToken == tokenHash);

        if (session != null)
        {
            // Revoke the entire family so no rotated token from this login remains usable.
            await RevokeFamilyAsync(session.UserId, session.FamilyId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Marks every non-revoked token in a family as revoked.
    /// </summary>
    private async Task RevokeFamilyAsync(int userId, string familyId)
    {
        var now = DateTime.UtcNow;
        var familySessions = await _context.Sessions
            .Where(s => s.UserId == userId && s.FamilyId == familyId && s.RevokedAt == null)
            .ToListAsync();

        foreach (var s in familySessions)
        {
            s.RevokedAt = now;
        }

        await _context.SaveChangesAsync();
    }

    public async Task<(bool Success, string? AccessToken, string? RefreshToken, DateTime? RefreshExpiresAt)> ChangePasswordAsync(int userId, string currentPassword, string newPassword, string ipAddress, string userAgent)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null || user.PasswordHash == null || !_passwordHasher.VerifyPassword(currentPassword, user.PasswordHash))
        {
            return (false, null, null, null);
        }

        user.PasswordHash = _passwordHasher.HashPassword(newPassword);
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.UtcNow;

        // Rotate the security stamp so all previously issued access tokens are rejected,
        // and drop the cached copy so it takes effect immediately.
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        SecurityStampCache.Invalidate(_cache, user.Id);

        // Invalidate all OLD refresh sessions for security
        var sessions = await _context.Sessions.Where(s => s.UserId == userId).ToListAsync();
        _context.Sessions.RemoveRange(sessions);

        // Create a new session (new family) with new tokens
        var accessToken = _jwtService.GenerateAccessToken(user);
        var refreshToken = _jwtService.GenerateRefreshToken();
        var expiresAt = DateTime.UtcNow.AddDays(
            int.Parse(_configuration["Jwt:RefreshExpirationDays"] ?? "1"));

        var newSession = new Session
        {
            UserId = user.Id,
            RefreshToken = HashToken(refreshToken),
            FamilyId = Guid.NewGuid().ToString("N"),
            IsUsed = false,
            ExpiresAt = expiresAt,
            IpAddress = ipAddress,
            DeviceInfo = userAgent,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };

        _context.Sessions.Add(newSession);
        await _context.SaveChangesAsync();

        return (true, accessToken, refreshToken, expiresAt);
    }

    public async Task<bool> AddEmailAsync(int userId, string email)
    {
        var user = await _context.Users.FindAsync(userId);

        if (user == null)
        {
            return false;
        }

        // Check if email is already in use by another user
        var emailExists = await _context.Users
            .AnyAsync(u => u.Email == email && u.Id != userId);

        if (emailExists)
        {
            return false;
        }

        user.Email = email;
        user.MustAddEmail = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Removes sessions that are expired or were revoked more than the grace period ago.
    /// Used/expired rows are kept briefly so reuse detection still works right after rotation.
    /// </summary>
    public async Task<int> CleanupExpiredSessionsAsync(TimeSpan? revokedGrace = null)
    {
        var grace = revokedGrace ?? TimeSpan.FromDays(1);
        var now = DateTime.UtcNow;
        var revokedCutoff = now - grace;

        var stale = await _context.Sessions
            .Where(s => s.ExpiresAt < now || (s.RevokedAt != null && s.RevokedAt < revokedCutoff))
            .ToListAsync();

        if (stale.Count == 0)
            return 0;

        _context.Sessions.RemoveRange(stale);
        await _context.SaveChangesAsync();
        return stale.Count;
    }
}
