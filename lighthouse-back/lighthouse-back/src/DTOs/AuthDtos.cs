namespace Lighthouse.DTOs;

public record LoginRequest(string Username, string Password, bool RememberMe = false);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    string Username,
    string Role,
    bool MustChangePassword,
    bool MustAddEmail
);

// RefreshToken is optional: browsers send it via the HttpOnly `lh_refresh` cookie.
// The body field is kept for non-browser clients / backward compatibility.
public record RefreshTokenRequest(string? RefreshToken = null);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record ForgotPasswordRequest(string UsernameOrEmail);

public record ResetPasswordRequest(string Token, string NewPassword);

public record AddEmailRequest(string Email);

public record UserDto(
    int Id,
    string Username,
    string? Email,
    string Role,
    bool IsEnabled,
    bool MustChangePassword,
    bool MustAddEmail,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    bool OidcLinked = false,
    bool HasPassword = true
);

/// <param name="Password">Null/empty creates an OIDC-only account (allowed only when OIDC is enabled).</param>
public record CreateUserRequest(
    string Username,
    string? Password,
    string Role,
    List<ResourcePermissionInput>? Permissions = null,
    string? Email = null
);

public record UpdateUserRequest(
    string? Username = null,
    string? Email = null,
    string? Role = null,
    bool? IsEnabled = null,
    bool? MustAddEmail = null,
    bool? MustChangePassword = null,
    string? NewPassword = null,
    List<ResourcePermissionInput>? Permissions = null
);

public record UpdateProfileRequest(string? Username = null);

/// <summary>
/// Generic paginated response
/// </summary>
public record PaginatedResponse<T>(
    List<T> Items,
    int PageNumber,
    int PageSize,
    int TotalPages,
    int TotalItems,
    bool HasNext,
    bool HasPrevious
);
