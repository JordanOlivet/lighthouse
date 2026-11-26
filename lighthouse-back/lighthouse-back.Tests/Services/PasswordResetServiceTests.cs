using DockerComposeManager.Services.Security;
using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.Data;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

public class PasswordResetServiceTests
{
    [Fact]
    public async Task CreateResetTokenAsync_UserWithoutPassword_CreatesNoToken()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var ctx = new AppDbContext(options);
        ctx.Roles.Add(new Role { Id = 1, Name = "user", Permissions = "[]" });
        ctx.Users.Add(new User
        {
            Id = 1,
            Username = "bob",
            Email = "bob@x.fr",
            PasswordHash = null,
            RoleId = 1,
            IsEnabled = true
        });
        await ctx.SaveChangesAsync();

        var email = new Mock<IEmailService>();
        var svc = new PasswordResetService(
            ctx,
            email.Object,
            Mock.Of<IPasswordHasher>(),
            NullLogger<PasswordResetService>.Instance,
            Options.Create(new PasswordResetOptions()),
            new MemoryCache(new MemoryCacheOptions()));

        var (success, error) = await svc.CreateResetTokenAsync("bob@x.fr", "1.2.3.4");

        success.Should().BeTrue("the response must stay generic to prevent enumeration");
        error.Should().BeNull();
        (await ctx.PasswordResetTokens.CountAsync()).Should().Be(0);
        email.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetPasswordAsync_RemovesTheSsoLink()
    {
        // Reset is the recovery path after a compromise: an identity linked by the intruder must go.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var ctx = new AppDbContext(options);
        ctx.Roles.Add(new Role { Id = 1, Name = "user", Permissions = "[]" });
        ctx.Users.Add(new User
        {
            Id = 1, Username = "bob", Email = "bob@x.fr", PasswordHash = "old", RoleId = 1, IsEnabled = true,
            OidcIssuer = "https://id.example.com", OidcSubject = "intruder", SecurityStamp = "s"
        });
        await ctx.SaveChangesAsync();

        string? token = null;
        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendPasswordResetEmailAsync("bob@x.fr", "bob", It.IsAny<string>(), It.IsAny<DateTime>()))
            .Callback<string, string, string, DateTime>((_, _, t, _) => token = t)
            .Returns(Task.CompletedTask);
        var svc = new PasswordResetService(ctx, email.Object, Mock.Of<IPasswordHasher>(),
            NullLogger<PasswordResetService>.Instance, Options.Create(new PasswordResetOptions()),
            new MemoryCache(new MemoryCacheOptions()));
        await svc.CreateResetTokenAsync("bob", "1.2.3.4");

        var (success, _) = await svc.ResetPasswordAsync(token!, "Nouveau-Kx7q-Zt92!", "1.2.3.4");

        success.Should().BeTrue();
        var bob = await ctx.Users.SingleAsync(u => u.Id == 1);
        bob.OidcIssuer.Should().BeNull();
        bob.OidcSubject.Should().BeNull();
    }
}
