using DockerComposeManager.Services.Security;
using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.DTOs;
using Lighthouse.Exceptions;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.Oidc;
using Lighthouse.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Lighthouse.Tests.Services;

/// <summary>OIDC account rules on real SQLite (case sensitivity, indexes, concurrent requests).</summary>
public class OidcSqliteTests : IDisposable
{
    private const string Iss = "https://id.example.com";
    private const int UserRole = 2;
    private readonly SqliteTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private void AddUser(int id, string username, string? email, string? passwordHash = null,
        string? sub = null, int roleId = UserRole)
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = id, Username = username, Email = email, PasswordHash = passwordHash, RoleId = roleId,
            IsEnabled = true, OidcIssuer = sub == null ? null : Iss, OidcSubject = sub, SecurityStamp = "s"
        });
        ctx.SaveChanges();
    }

    private static OidcUserResolver Resolver(Lighthouse.Data.AppDbContext ctx, bool autoProvision = false) =>
        new(ctx, Options.Create(new OidcOptions { Authority = Iss, ClientId = "lh", AutoProvision = autoProvision }),
            NullLogger<OidcUserResolver>.Instance);

    private static OidcIdentity Identity(string sub = "sub-1", string email = "alice@x.fr", string? preferred = "alice") =>
        new(Iss, sub, email, true, preferred);

    private static AuthService NewAuthService(Lighthouse.Data.AppDbContext ctx)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-key-at-least-32-characters-long",
            ["Jwt:Issuer"] = "lighthouse",
            ["Jwt:Audience"] = "lighthouse-client"
        }).Build();
        return new AuthService(ctx, new JwtTokenService(config), config, Mock.Of<IPasswordHasher>(),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<AuthService>.Instance);
    }

    // --- Email uniqueness is case-insensitive everywhere (else a case variant blocks SSO linking) ---

    [Fact]
    public async Task AddEmail_CaseVariantOfAnotherAccountsEmail_IsRefused()
    {
        AddUser(10, "alice", "alice@example.com");
        AddUser(11, "mallory", null, passwordHash: "h");
        using var ctx = _db.NewContext();

        (await NewAuthService(ctx).AddEmailAsync(11, "Alice@Example.com")).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateUser_EmailCaseVariantOfAnotherAccount_ThrowsConflict()
    {
        AddUser(10, "alice", "alice@example.com");
        AddUser(11, "mallory", null, passwordHash: "h");
        using var ctx = _db.NewContext();
        var service = new UserService(ctx, NullLogger<UserService>.Instance, Mock.Of<IPasswordHasher>(),
            new MemoryCache(new MemoryCacheOptions()));

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateUserAsync(11, new UpdateUserRequest(Email: "ALICE@example.com")));
    }

    [Fact]
    public async Task Resolve_EmailMatch_IsCaseInsensitiveOnSqlite()
    {
        AddUser(10, "alice", "Alice@X.fr");
        using var ctx = _db.NewContext();

        var result = await Resolver(ctx).ResolveAsync(Identity(email: "alice@x.fr"));

        result.Success.Should().BeTrue();
        result.User!.OidcSubject.Should().Be("sub-1");
    }

    [Fact]
    public async Task Resolve_LegacyCaseVariantDuplicates_ReturnsEmailAmbiguous()
    {
        AddUser(10, "alice", "alice@x.fr");
        AddUser(11, "alice2", "ALICE@x.fr");
        using var ctx = _db.NewContext();

        var result = await Resolver(ctx).ResolveAsync(Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.EmailAmbiguous);
    }

    [Fact]
    public void FilteredUniqueIndex_AllowsManyUnlinked_RejectsDuplicateIdentity()
    {
        AddUser(10, "a", "a@x.fr");
        AddUser(11, "b", "b@x.fr");
        AddUser(12, "c", "c@x.fr", sub: "sub-1");

        var act = () => AddUser(13, "d", "d@x.fr", sub: "sub-1");

        act.Should().Throw<DbUpdateException>();
    }

    // --- Only password-less (SSO-only) accounts are linked by email ---------------------

    [Fact]
    public async Task Resolve_EmailMatch_AccountWithPassword_ReturnsLinkRequiredWithoutLinking()
    {
        AddUser(10, "alice", "alice@x.fr", passwordHash: "h");
        using var ctx = _db.NewContext();

        var result = await Resolver(ctx).ResolveAsync(Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.LinkRequired);
        (await ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 10)).OidcSubject.Should().BeNull();
    }

    // --- Auto-provisioning ---------------------------------------------------------------

    [Fact]
    public async Task Provision_PreferredUsernameCaseVariantOfExisting_GetsSuffix()
    {
        using var ctx = _db.NewContext();

        // The seeded built-in account is "admin".
        var result = await Resolver(ctx, autoProvision: true).ResolveAsync(Identity(email: "boss@x.fr", preferred: "Admin"));

        result.User!.Username.Should().Be("Admin2");
    }

    [Fact]
    public async Task Provision_ConcurrentFirstLoginOfSameIdentity_ReturnsTheWinnersAccount()
    {
        // Another request provisions the same identity between our lookup and our insert.
        using var ctx = _db.NewContext(new BeforeSaveOnce(() => _db.Execute(
            "INSERT INTO Users (Id, Username, Email, RoleId, IsEnabled, MustChangePassword, MustAddEmail, CreatedAt, SecurityStamp, OidcIssuer, OidcSubject) " +
            $"VALUES (50, 'alice', 'alice@x.fr', 2, 1, 0, 0, '2026-01-01', 's', '{Iss}', 'sub-1')")));

        var result = await Resolver(ctx, autoProvision: true).ResolveAsync(Identity(email: "alice@x.fr"));

        result.Success.Should().BeTrue();
        result.User!.Id.Should().Be(50);
    }

    [Fact]
    public async Task Link_ConcurrentLinkOfSameIdentity_ReturnsIdentityInUse()
    {
        AddUser(10, "alice", "alice@x.fr", passwordHash: "h");
        AddUser(11, "bob", "bob@x.fr", passwordHash: "h");
        using var ctx = _db.NewContext(new BeforeSaveOnce(() =>
            _db.Execute($"UPDATE Users SET OidcIssuer = '{Iss}', OidcSubject = 'sub-1' WHERE Id = 11")));

        var result = await Resolver(ctx).LinkAsync(10, Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.IdentityInUse);
    }

    // --- Emails are stored lowercased (SQLite lower() only folds ASCII) -----------------

    private UserService NewUserService(Lighthouse.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<UserService>.Instance, Mock.Of<IPasswordHasher>(), new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task SsoOnlyAccountWithNonAsciiUppercaseEmail_IsLinkedByEmail()
    {
        using (var setup = _db.NewContext())
            await NewUserService(setup).CreateUserAsync(new CreateUserRequest("elodie", null, "user", Email: "Élodie@X.fr"));
        using var ctx = _db.NewContext();

        var result = await Resolver(ctx).ResolveAsync(Identity(email: "Élodie@x.fr", preferred: "elodie"));

        result.Success.Should().BeTrue();
        result.User!.Username.Should().Be("elodie");
    }

    [Fact]
    public async Task AddEmail_StoresEmailLowercased()
    {
        AddUser(11, "bob", null, passwordHash: "h");
        using (var ctx = _db.NewContext())
            (await NewAuthService(ctx).AddEmailAsync(11, "Élodie@X.fr")).Should().BeTrue();

        using var check = _db.NewContext();
        (await check.Users.SingleAsync(u => u.Id == 11)).Email.Should().Be("élodie@x.fr");
    }

    [Fact]
    public async Task UpdateUser_StoresEmailLowercased()
    {
        AddUser(11, "bob", "bob@x.fr", passwordHash: "h");
        using (var ctx = _db.NewContext())
            await NewUserService(ctx).UpdateUserAsync(11, new UpdateUserRequest(Email: "Élodie@X.fr"));

        using var check = _db.NewContext();
        (await check.Users.SingleAsync(u => u.Id == 11)).Email.Should().Be("élodie@x.fr");
    }

    // --- Only unique-index conflicts count as a lost race -------------------------------

    [Fact]
    public async Task EmailLink_WriteFailureOtherThanConstraint_IsNotTreatedAsLostRace()
    {
        AddUser(10, "alice", "alice@x.fr");
        using var ctx = _db.NewContext(new BeforeSaveOnce(() => throw new DbUpdateException("disk I/O error")));

        await Assert.ThrowsAsync<DbUpdateException>(() => Resolver(ctx).ResolveAsync(Identity()));
    }

    [Fact]
    public async Task Provision_StoresEmailLowercased()
    {
        using (var ctx = _db.NewContext())
            (await Resolver(ctx, autoProvision: true).ResolveAsync(Identity(email: "Élodie@X.fr", preferred: "elodie")))
                .Success.Should().BeTrue();

        using var check = _db.NewContext();
        (await check.Users.SingleAsync(u => u.Username == "elodie")).Email.Should().Be("élodie@x.fr");
    }

    [Fact]
    public async Task ForgotPassword_EmailTypedWithCapitals_FindsTheLowercasedAccount()
    {
        AddUser(10, "alice", null, passwordHash: "h");
        using (var setup = _db.NewContext())
            (await NewAuthService(setup).AddEmailAsync(10, "Alice@Example.com")).Should().BeTrue();
        using var ctx = _db.NewContext();
        var email = new Mock<Lighthouse.Services.Email.IEmailService>();
        var svc = new PasswordResetService(ctx, email.Object, Mock.Of<IPasswordHasher>(),
            NullLogger<PasswordResetService>.Instance, Options.Create(new PasswordResetOptions()),
            new MemoryCache(new MemoryCacheOptions()));

        await svc.CreateResetTokenAsync("Alice@Example.com", "1.2.3.4");

        (await ctx.PasswordResetTokens.CountAsync()).Should().Be(1);
    }
}
