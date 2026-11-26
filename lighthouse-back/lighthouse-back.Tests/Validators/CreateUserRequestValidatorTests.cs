using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Validators;
using Microsoft.Extensions.Options;

namespace Lighthouse.Tests.Validators;

public class CreateUserRequestValidatorTests
{
    private static CreateUserRequestValidator Validator(bool oidcEnabled) =>
        new(Options.Create(oidcEnabled
            ? new OidcOptions { Authority = "https://id.example.com", ClientId = "lh" }
            : new OidcOptions()));

    [Fact]
    public void NoPassword_OidcDisabled_Invalid()
    {
        var result = Validator(false).Validate(new CreateUserRequest("bob", null, "user", Email: "bob@x.fr"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }

    [Fact]
    public void NoPassword_OidcEnabled_WithEmail_Valid()
    {
        var result = Validator(true).Validate(new CreateUserRequest("bob", null, "user", Email: "bob@x.fr"));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void EmptyPassword_OidcEnabled_WithEmail_Valid()
    {
        Validator(true).Validate(new CreateUserRequest("bob", "", "user", Email: "bob@x.fr"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void NoPassword_OidcEnabled_WithoutEmail_Invalid()
    {
        var result = Validator(true).Validate(new CreateUserRequest("bob", null, "user"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }

    [Fact]
    public void InvalidEmail_Invalid()
    {
        var result = Validator(false).Validate(new CreateUserRequest("bob", "Password1!", "user", Email: "not-an-email"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("Admin")]
    public void AdminWithoutPassword_IsInvalid_EvenWithSso(string role)
    {
        var result = Validator(true).Validate(new CreateUserRequest("root2", null, role, Email: "root2@x.fr"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }
}
