using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Microsoft.Extensions.Options;
using FluentValidation;

namespace Lighthouse.Validators;

/// <summary>
/// Validator for login requests
/// </summary>
public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required")
            .Length(3, 100).WithMessage("Username must be between 3 and 100 characters");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required");

        // Only enforce minimum length in production
        When(x => ValidationConfig.ShouldEnforceStrictPasswordRules, () =>
        {
            RuleFor(x => x.Password)
                .MinimumLength(PasswordRules.MinLength).WithMessage($"Password must be at least {PasswordRules.MinLength} characters");
        });
    }
}

/// <summary>
/// Validator for refresh token requests
/// </summary>
public class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        // The refresh token is normally carried by the HttpOnly `lh_refresh` cookie, so the
        // body value is optional. When a non-browser client does supply it in the body,
        // enforce a minimum length; the controller rejects a genuinely missing token.
        RuleFor(x => x.RefreshToken)
            .MinimumLength(20).WithMessage("Invalid refresh token format")
            .When(x => !string.IsNullOrEmpty(x.RefreshToken));
    }
}

/// <summary>
/// Validator for change password requests
/// </summary>
public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty().WithMessage("Current password is required");

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("New password is required")
            .NotEqual(x => x.CurrentPassword)
            .WithMessage("New password must be different from current password");

        // Only enforce strict password rules in production
        When(x => ValidationConfig.ShouldEnforceStrictPasswordRules, () =>
        {
            RuleFor(x => x.NewPassword)
                .ApplyPasswordRules();
        });
    }
}

/// <summary>
/// Validator for create user requests
/// </summary>
public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator(IOptions<OidcOptions> oidcOptions)
    {
        var oidcEnabled = oidcOptions.Value.IsEnabled;

        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required")
            .Length(3, 100).WithMessage("Username must be between 3 and 100 characters")
            .Matches(@"^[a-zA-Z0-9_-]+$").WithMessage("Username can only contain letters, numbers, hyphens and underscores");

        if (oidcEnabled)
        {
            // No password = OIDC-only account, which is linked by email on first SSO login.
            When(x => string.IsNullOrEmpty(x.Password), () =>
            {
                RuleFor(x => x.Email)
                    .NotEmpty().WithMessage("Email is required for an account without password (SSO login only)");
            });
        }
        else
        {
            RuleFor(x => x.Password)
                .NotEmpty().WithMessage("Password is required");
        }

        // Only enforce strict password rules in production
        When(x => ValidationConfig.ShouldEnforceStrictPasswordRules && !string.IsNullOrEmpty(x.Password), () =>
        {
            RuleFor(x => x.Password!)
                .ApplyPasswordRules();
        });

        When(x => !string.IsNullOrEmpty(x.Email), () =>
        {
            RuleFor(x => x.Email)
                .EmailAddress().WithMessage("Invalid email address")
                .MaximumLength(255).WithMessage("Email cannot exceed 255 characters");
        });

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required")
            .Must(role => role == "admin" || role == "user")
            .WithMessage("Role must be 'admin' or 'user'");
    }
}

/// <summary>
/// Validator for update user requests
/// </summary>
public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        When(x => !string.IsNullOrEmpty(x.Username), () =>
        {
            RuleFor(x => x.Username)
                .Length(3, 100).WithMessage("Username must be between 3 and 100 characters")
                .Matches(@"^[a-zA-Z0-9_-]+$").WithMessage("Username can only contain letters, numbers, hyphens and underscores");
        });

        When(x => !string.IsNullOrEmpty(x.Email), () =>
        {
            RuleFor(x => x.Email)
                .EmailAddress().WithMessage("Invalid email address")
                .MaximumLength(255).WithMessage("Email cannot exceed 255 characters");
        });

        When(x => !string.IsNullOrEmpty(x.Role), () =>
        {
            RuleFor(x => x.Role)
                .Must(role => role == "admin" || role == "user")
                .WithMessage("Role must be 'admin' or 'user'");
        });

        When(x => !string.IsNullOrEmpty(x.NewPassword), () =>
        {
            // Apply same rules conditionally on environment, like all other validators
            When(x => ValidationConfig.ShouldEnforceStrictPasswordRules, () =>
            {
                RuleFor(x => x.NewPassword!)
                    .ApplyPasswordRules();
            });
        });
    }
}

/// <summary>
/// Validator for update profile requests
/// </summary>
public class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        When(x => !string.IsNullOrEmpty(x.Username), () =>
        {
            RuleFor(x => x.Username)
                .Length(3, 100).WithMessage("Username must be between 3 and 100 characters")
                .Matches(@"^[a-zA-Z0-9_-]+$").WithMessage("Username can only contain letters, numbers, hyphens and underscores");
        });
    }
}

/// <summary>
/// Validator for forgot password requests
/// </summary>
public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.UsernameOrEmail)
            .NotEmpty().WithMessage("Username or email is required")
            .MinimumLength(3).WithMessage("Username or email must be at least 3 characters");
    }
}

/// <summary>
/// Validator for reset password requests
/// </summary>
public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("Reset token is required")
            .MinimumLength(20).WithMessage("Invalid reset token format");

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("New password is required");

        // Only enforce strict password rules in production
        When(x => ValidationConfig.ShouldEnforceStrictPasswordRules, () =>
        {
            RuleFor(x => x.NewPassword)
                .ApplyPasswordRules();
        });
    }
}

/// <summary>
/// Validator for add email requests
/// </summary>
public class AddEmailRequestValidator : AbstractValidator<AddEmailRequest>
{
    public AddEmailRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Invalid email format")
            .MaximumLength(255).WithMessage("Email must not exceed 255 characters");
    }
}
