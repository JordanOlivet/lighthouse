using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Lighthouse.Configuration;
using Lighthouse.Data;
using Lighthouse.Models;
using Lighthouse.Services.Email;
using Lighthouse.Services.Security;
using DockerComposeManager.Services.Security;

namespace Lighthouse.Services;

public class PasswordResetService : IPasswordResetService
{
    private readonly AppDbContext _context;
    private readonly IEmailService _emailService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<PasswordResetService> _logger;
    private readonly PasswordResetOptions _options;
    private readonly IMemoryCache _cache;

    public PasswordResetService(
        AppDbContext context,
        IEmailService emailService,
        IPasswordHasher passwordHasher,
        ILogger<PasswordResetService> logger,
        IOptions<PasswordResetOptions> options,
        IMemoryCache cache)
    {
        _context = context;
        _emailService = emailService;
        _passwordHasher = passwordHasher;
        _logger = logger;
        _options = options.Value;
        _cache = cache;
    }

    public async Task<(bool Success, string? Error)> CreateResetTokenAsync(string usernameOrEmail, string ipAddress)
    {
        try
        {
            // Find user by username OR email
            var user = await _context.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Username == usernameOrEmail || u.Email == usernameOrEmail);

            // ALWAYS return success to prevent user enumeration
            if (user == null)
            {
                _logger.LogWarning("Password reset requested for non-existent user: {UsernameOrEmail} from IP: {IpAddress}",
                    usernameOrEmail, ipAddress);
                // Still return success to prevent enumeration
                return (true, null);
            }

            // Check if user has email
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                _logger.LogWarning("Password reset requested for user without email: {Username} from IP: {IpAddress}",
                    user.Username, ipAddress);
                // Still return success to prevent enumeration
                return (true, null);
            }

            // OIDC-only accounts have no local password to reset.
            if (user.PasswordHash == null)
            {
                _logger.LogWarning("Password reset requested for OIDC-only user: {Username} from IP: {IpAddress}",
                    user.Username, ipAddress);
                // Still return success to prevent enumeration
                return (true, null);
            }

            // Check if user is enabled
            if (!user.IsEnabled)
            {
                _logger.LogWarning("Password reset requested for disabled user: {Username} from IP: {IpAddress}",
                    user.Username, ipAddress);
                // Still return success to prevent enumeration
                return (true, null);
            }

            // Check max active tokens per user
            var activeTokensCount = await _context.PasswordResetTokens
                .CountAsync(t => t.UserId == user.Id && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow);

            if (activeTokensCount >= _options.MaxActiveTokensPerUser)
            {
                _logger.LogWarning("User {Username} has reached max active tokens ({Max}). Deleting oldest.",
                    user.Username, _options.MaxActiveTokensPerUser);

                // Delete oldest token to make room
                var oldestToken = await _context.PasswordResetTokens
                    .Where(t => t.UserId == user.Id && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow)
                    .OrderBy(t => t.CreatedAt)
                    .FirstOrDefaultAsync();

                if (oldestToken != null)
                {
                    _context.PasswordResetTokens.Remove(oldestToken);
                }
            }

            // Generate 32-byte random token
            var tokenBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(tokenBytes);
            }
            var token = Convert.ToBase64String(tokenBytes).Replace("+", "-").Replace("/", "_").Replace("=", "");

            // Compute SHA256 hash for storage
            var tokenHash = ComputeSha256Hash(token);

            // Create token record
            var resetToken = new PasswordResetToken
            {
                UserId = user.Id,
                TokenHash = tokenHash,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(_options.TokenExpirationMinutes),
                IpAddress = ipAddress
            };

            _context.PasswordResetTokens.Add(resetToken);
            await _context.SaveChangesAsync();

            // Send email (don't throw on failure)
            try
            {
                await _emailService.SendPasswordResetEmailAsync(user.Email, user.Username, token, resetToken.ExpiresAt);
                _logger.LogInformation("Password reset email sent to user: {Username} ({Email})", user.Username, user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
                // Don't fail the request if email fails - token is already created
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating password reset token for {UsernameOrEmail}", usernameOrEmail);
            return (false, "An error occurred while processing your request. Please try again later.");
        }
    }

    public async Task<(bool Success, int? UserId, string? Error)> ValidateTokenAsync(string token)
    {
        try
        {
            // Hash incoming token
            var tokenHash = ComputeSha256Hash(token);

            // Find token in database
            var resetToken = await _context.PasswordResetTokens
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

            if (resetToken == null)
            {
                return (false, null, "Invalid or expired reset token.");
            }

            // Check if already used
            if (resetToken.IsUsed)
            {
                return (false, null, "This reset token has already been used.");
            }

            // Check if expired
            if (resetToken.ExpiresAt < DateTime.UtcNow)
            {
                return (false, null, "This reset token has expired. Please request a new one.");
            }

            // Check if user is enabled
            if (!resetToken.User.IsEnabled)
            {
                return (false, null, "This account is disabled.");
            }

            return (true, resetToken.UserId, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating password reset token");
            return (false, null, "An error occurred while validating the token.");
        }
    }

    public async Task<(bool Success, string? Error)> ResetPasswordAsync(string token, string newPassword, string ipAddress)
    {
        try
        {
            // Validate token
            var (isValid, userId, validationError) = await ValidateTokenAsync(token);
            if (!isValid || userId == null)
            {
                return (false, validationError ?? "Invalid token.");
            }

            // Get user
            var user = await _context.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return (false, "User not found.");
            }

            // Hash new password
            var newPasswordHash = _passwordHasher.HashPassword(newPassword);

            // Update user password and clear MustChangePassword
            user.PasswordHash = newPasswordHash;
            user.MustChangePassword = false;
            user.UpdatedAt = DateTime.UtcNow;

            // Reset is the recovery path after a compromise: drop an SSO link the intruder may
            // have added from an open session. The owner re-links from the user menu.
            user.OidcIssuer = null;
            user.OidcSubject = null;

            // Rotate the security stamp so any outstanding access tokens are rejected.
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            SecurityStampCache.Invalidate(_cache, user.Id);

            // Mark token as used
            var tokenHash = ComputeSha256Hash(token);
            var resetToken = await _context.PasswordResetTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

            if (resetToken != null)
            {
                resetToken.IsUsed = true;
                resetToken.UsedAt = DateTime.UtcNow;
            }

            // Invalidate all sessions for security
            var sessions = await _context.Sessions.Where(s => s.UserId == userId.Value).ToListAsync();
            _context.Sessions.RemoveRange(sessions);

            await _context.SaveChangesAsync();

            _logger.LogInformation("Password reset successful for user: {Username} from IP: {IpAddress}",
                user.Username, ipAddress);

            // Send confirmation email (don't throw on failure)
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                try
                {
                    await _emailService.SendPasswordChangedConfirmationAsync(user.Email, user.Username);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send password changed confirmation to {Email}", user.Email);
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return (false, "An error occurred while resetting your password. Please try again later.");
        }
    }

    public async Task<int> CleanupExpiredTokensAsync()
    {
        try
        {
            // Delete tokens expired more than 24 hours ago
            var cutoffDate = DateTime.UtcNow.AddHours(-24);
            var expiredTokens = await _context.PasswordResetTokens
                .Where(t => t.ExpiresAt < cutoffDate)
                .ToListAsync();

            if (expiredTokens.Any())
            {
                _context.PasswordResetTokens.RemoveRange(expiredTokens);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Cleaned up {Count} expired password reset tokens", expiredTokens.Count);
                return expiredTokens.Count;
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired password reset tokens");
            return 0;
        }
    }

    private static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLower();
    }
}
