using FluentAssertions;
using Lighthouse.Configuration;
using Lighthouse.Data;
using Lighthouse.Models;
using Lighthouse.Services.Oidc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lighthouse.Tests.Services;

public class OidcUserResolverTests
{
    private const string Iss = "https://id.example.com";
    private const int AdminRoleId = 1;
    private const int UserRoleId = 2;

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var ctx = new AppDbContext(options);
        ctx.Roles.Add(new Role { Id = 1, Name = "admin", Permissions = "[]" });
        ctx.Roles.Add(new Role { Id = 2, Name = "user", Permissions = "[]" });
        ctx.SaveChanges();
        return ctx;
    }

    private static User AddUser(AppDbContext ctx, int id, string username, string? email,
        bool enabled = true, string? sub = null, int roleId = UserRoleId, string? passwordHash = null)
    {
        var user = new User
        {
            Id = id,
            Username = username,
            Email = email,
            PasswordHash = passwordHash,
            RoleId = roleId,
            IsEnabled = enabled,
            OidcIssuer = sub == null ? null : Iss,
            OidcSubject = sub
        };
        ctx.Users.Add(user);
        ctx.SaveChanges();
        return user;
    }

    private static OidcUserResolver NewResolver(AppDbContext ctx, OidcOptions? options = null)
        => new(ctx, Options.Create(options ?? new OidcOptions { Authority = Iss, ClientId = "lh" }),
            NullLogger<OidcUserResolver>.Instance);

    private static OidcIdentity Identity(string sub = "sub-1", string? email = "alice@x.fr",
        bool verified = true, string? preferredUsername = "alice")
        => new(Iss, sub, email, verified, preferredUsername);

    [Fact]
    public async Task Resolve_KnownSubject_ReturnsLinkedUser()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr", sub: "sub-1");

        // Email changed at the provider: the subject link still wins.
        var result = await NewResolver(ctx).ResolveAsync(Identity(email: "other@x.fr", verified: false));

        result.Success.Should().BeTrue();
        result.User!.Id.Should().Be(1);
        result.User.Role.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_KnownSubject_DisabledUser_ReturnsAccountDisabled()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr", enabled: false, sub: "sub-1");

        var result = await NewResolver(ctx).ResolveAsync(Identity());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OidcErrorCodes.AccountDisabled);
    }

    [Fact]
    public async Task Resolve_NoEmail_ReturnsEmailMissing()
    {
        using var ctx = NewContext();
        var result = await NewResolver(ctx).ResolveAsync(Identity(email: null));
        result.ErrorCode.Should().Be(OidcErrorCodes.EmailMissing);
    }

    [Fact]
    public async Task Resolve_UnverifiedEmail_ReturnsEmailNotVerified()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr");

        var result = await NewResolver(ctx).ResolveAsync(Identity(verified: false));

        result.ErrorCode.Should().Be(OidcErrorCodes.EmailNotVerified);
        (await ctx.Users.SingleAsync()).OidcSubject.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_UnverifiedEmail_WhenNotRequired_LinksAccount()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr");
        var options = new OidcOptions { Authority = Iss, ClientId = "lh", RequireVerifiedEmail = false };

        var result = await NewResolver(ctx, options).ResolveAsync(Identity(verified: false));

        result.Success.Should().BeTrue();
        result.User!.OidcSubject.Should().Be("sub-1");
    }

    [Fact]
    public async Task Resolve_EmailMatchCaseInsensitive_LinksIssuerAndSubject()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr");

        var result = await NewResolver(ctx).ResolveAsync(Identity(email: "Alice@X.fr"));

        result.Success.Should().BeTrue();
        var stored = await ctx.Users.AsNoTracking().SingleAsync();
        stored.OidcIssuer.Should().Be(Iss);
        stored.OidcSubject.Should().Be("sub-1");
    }

    [Fact]
    public async Task Resolve_EmailMatch_DisabledUser_ReturnsAccountDisabled()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr", enabled: false);

        var result = await NewResolver(ctx).ResolveAsync(Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.AccountDisabled);
        (await ctx.Users.AsNoTracking().SingleAsync()).OidcSubject.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_EmailMatch_AlreadyLinkedToOtherSubject_ReturnsSubjectMismatch()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr", sub: "old-sub");

        var result = await NewResolver(ctx).ResolveAsync(Identity(sub: "new-sub"));

        result.ErrorCode.Should().Be(OidcErrorCodes.SubjectMismatch);
        (await ctx.Users.AsNoTracking().SingleAsync()).OidcSubject.Should().Be("old-sub");
    }

    [Fact]
    public async Task Resolve_EmailMatchesSeveralAccounts_RefusesWithoutLinking()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr");
        AddUser(ctx, 2, "alice-other", "Alice@X.fr");

        var result = await NewResolver(ctx).ResolveAsync(Identity(email: "alice@x.fr"));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OidcErrorCodes.EmailAmbiguous);
        (await ctx.Users.AsNoTracking().CountAsync(u => u.OidcSubject != null)).Should().Be(0);
    }

    [Fact]
    public async Task Resolve_UnknownEmail_ReturnsAccountNotFound()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "alice", "alice@x.fr");

        var result = await NewResolver(ctx).ResolveAsync(Identity(email: "bob@x.fr", preferredUsername: "bob"));

        result.ErrorCode.Should().Be(OidcErrorCodes.AccountNotFound);
        (await ctx.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Resolve_UnknownEmail_AutoProvision_CreatesUserRole()
    {
        using var ctx = NewContext();
        var options = new OidcOptions { Authority = Iss, ClientId = "lh", AutoProvision = true };

        var result = await NewResolver(ctx, options).ResolveAsync(Identity(email: "carol@x.fr", preferredUsername: "carol"));

        result.Success.Should().BeTrue();
        var created = await ctx.Users.Include(u => u.Role).AsNoTracking().SingleAsync();
        created.Username.Should().Be("carol");
        created.Email.Should().Be("carol@x.fr");
        created.Role!.Name.Should().Be("user");
        created.PasswordHash.Should().BeNull();
        created.MustChangePassword.Should().BeFalse();
        created.MustAddEmail.Should().BeFalse();
        created.OidcIssuer.Should().Be(Iss);
        created.OidcSubject.Should().Be("sub-1");
    }

    [Fact]
    public async Task Resolve_AutoProvision_UsernameTaken_AppendsSuffix()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "carol", "someone-else@x.fr");
        var options = new OidcOptions { Authority = Iss, ClientId = "lh", AutoProvision = true };

        var result = await NewResolver(ctx, options).ResolveAsync(Identity(email: "carol@x.fr", preferredUsername: "carol"));

        result.User!.Username.Should().Be("carol2");
    }

    [Fact]
    public async Task Resolve_AutoProvision_NoPreferredUsername_UsesEmailLocalPart()
    {
        using var ctx = NewContext();
        var options = new OidcOptions { Authority = Iss, ClientId = "lh", AutoProvision = true };

        var result = await NewResolver(ctx, options).ResolveAsync(Identity(email: "dave@x.fr", preferredUsername: null));

        result.User!.Username.Should().Be("dave");
    }

    [Fact]
    public async Task Resolve_KnownSubject_Admin_ReturnsLinkedAdmin()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "root", "root@x.fr", sub: "sub-1", roleId: AdminRoleId);

        var result = await NewResolver(ctx).ResolveAsync(Identity(email: "root@x.fr"));

        result.Success.Should().BeTrue();
        result.User!.Id.Should().Be(1);
    }

    [Fact]
    public async Task Resolve_EmailMatch_UnlinkedAdmin_ReturnsAdminLinkRequiredWithoutLinking()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "root", "root@x.fr", roleId: AdminRoleId);

        var result = await NewResolver(ctx).ResolveAsync(Identity(email: "root@x.fr"));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OidcErrorCodes.AdminLinkRequired);
        (await ctx.Users.AsNoTracking().SingleAsync()).OidcSubject.Should().BeNull();
    }

    [Fact]
    public async Task Link_UnlinkedAccount_LinksWithoutComparingEmails()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "root", "root@x.fr", roleId: AdminRoleId);

        // Different and unverified email: an explicit link from an open session does not use it.
        var result = await NewResolver(ctx).LinkAsync(1, Identity(email: "someone@else.fr", verified: false));

        result.Success.Should().BeTrue();
        var stored = await ctx.Users.AsNoTracking().SingleAsync();
        stored.OidcIssuer.Should().Be(Iss);
        stored.OidcSubject.Should().Be("sub-1");
    }

    [Fact]
    public async Task Link_AlreadyLinkedToSameSubject_Succeeds()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "root", "root@x.fr", sub: "sub-1", roleId: AdminRoleId);

        var result = await NewResolver(ctx).LinkAsync(1, Identity());

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Link_AccountLinkedToOtherSubject_ReturnsSubjectMismatch()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "root", "root@x.fr", sub: "sub-old", roleId: AdminRoleId);

        var result = await NewResolver(ctx).LinkAsync(1, Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.SubjectMismatch);
        (await ctx.Users.AsNoTracking().SingleAsync()).OidcSubject.Should().Be("sub-old");
    }

    [Fact]
    public async Task Link_IdentityLinkedToAnotherAccount_ReturnsIdentityInUse()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "root", "root@x.fr", roleId: AdminRoleId);
        AddUser(ctx, 2, "alice", "alice@x.fr", sub: "sub-1");

        var result = await NewResolver(ctx).LinkAsync(1, Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.IdentityInUse);
        (await ctx.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).OidcSubject.Should().BeNull();
    }

    [Fact]
    public async Task Link_DisabledAccount_ReturnsAccountDisabled()
    {
        using var ctx = NewContext();
        AddUser(ctx, 1, "root", "root@x.fr", enabled: false, roleId: AdminRoleId);

        var result = await NewResolver(ctx).LinkAsync(1, Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.AccountDisabled);
    }

    [Fact]
    public async Task Link_UnknownAccount_ReturnsAccountNotFound()
    {
        using var ctx = NewContext();

        var result = await NewResolver(ctx).LinkAsync(42, Identity());

        result.ErrorCode.Should().Be(OidcErrorCodes.AccountNotFound);
    }

    [Fact]
    public async Task Resolve_EmailMatch_UnlinkedAdminRoleOtherCase_ReturnsAdminLinkRequired()
    {
        using var ctx = NewContext();
        ctx.Roles.Add(new Role { Id = 3, Name = "Admin", Permissions = "[]" });
        ctx.SaveChanges();
        AddUser(ctx, 1, "root", "root@x.fr", roleId: 3);

        var result = await NewResolver(ctx).ResolveAsync(Identity(email: "root@x.fr"));

        result.ErrorCode.Should().Be(OidcErrorCodes.AdminLinkRequired);
    }
}
