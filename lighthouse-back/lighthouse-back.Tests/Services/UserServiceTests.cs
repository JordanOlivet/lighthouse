using Lighthouse.Data;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.Security;
using Lighthouse.Exceptions;
using DockerComposeManager.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace Lighthouse.Tests.Services;

public class UserServiceTests
{
    private AppDbContext GetInMemoryDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var context = new AppDbContext(options);

        // Seed default roles
        context.Roles.AddRange(
            new Role { Id = 1, Name = "admin", Permissions = "[\"all\"]" },
            new Role { Id = 2, Name = "user", Permissions = "[\"read\"]" }
        );
        context.SaveChanges();

        return context;
    }

    [Fact]
    public async Task GetAllUsersAsync_ReturnsAllUsers()
    {
        // Arrange
        AppDbContext context = GetInMemoryDbContext();
        var logger = new Mock<ILogger<UserService>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        context.Users.AddRange(
            new User { Id = 1, Username = "admin", PasswordHash = "hash1", RoleId = 1, IsEnabled = true, CreatedAt = DateTime.UtcNow },
            new User { Id = 2, Username = "user1", PasswordHash = "hash2", RoleId = 2, IsEnabled = true, CreatedAt = DateTime.UtcNow }
        );
        await context.SaveChangesAsync();

        var service = new UserService(context, logger.Object, passwordHasher.Object, new MemoryCache(new MemoryCacheOptions()));

        // Act
        List<UserDto> users = await service.GetAllUsersAsync();

        // Assert
        Assert.Equal(2, users.Count);
        Assert.Contains(users, u => u.Username == "admin");
        Assert.Contains(users, u => u.Username == "user1");
    }

    [Fact]
    public async Task CreateUserAsync_CreatesUserSuccessfully()
    {
        // Arrange
        AppDbContext context = GetInMemoryDbContext();
        var logger = new Mock<ILogger<UserService>>();
        var passwordHasher = new Mock<IPasswordHasher>();
        passwordHasher.Setup(p => p.HashPassword(It.IsAny<string>())).Returns("hashed_password");
        var service = new UserService(context, logger.Object, passwordHasher.Object, new MemoryCache(new MemoryCacheOptions()));

        var request = new CreateUserRequest("testuser", "password123", "user");

        // Act
        var result = await service.CreateUserAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("testuser", result.Username);
        Assert.Equal("user", result.Role);
        Assert.True(result.IsEnabled);
        Assert.True(result.MustChangePassword);

        // Verify password was hashed
        User? user = await context.Users.FirstOrDefaultAsync(u => u.Username == "testuser");
        Assert.NotNull(user);
        Assert.NotEqual("password123", user.PasswordHash);
    }

    [Fact]
    public async Task CreateUserAsync_ThrowsWhenUsernameExists()
    {
        // Arrange
        AppDbContext context = GetInMemoryDbContext();
        var logger = new Mock<ILogger<UserService>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        context.Users.Add(new User
        {
            Username = "existinguser",
            PasswordHash = "hash",
            RoleId = 2,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new UserService(context, logger.Object, passwordHasher.Object, new MemoryCache(new MemoryCacheOptions()));
        var request = new CreateUserRequest("existinguser", "password123", "user");

        // Act & Assert
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateUserAsync(request));
    }

    [Fact]
    public async Task DeleteUserAsync_PreventsDeletingLastAdmin()
    {
        // Arrange
        AppDbContext context = GetInMemoryDbContext();
        var logger = new Mock<ILogger<UserService>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        var adminUser = new User
        {
            Id = 1,
            Username = "admin",
            PasswordHash = "hash",
            RoleId = 1,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(adminUser);
        await context.SaveChangesAsync();

        var service = new UserService(context, logger.Object, passwordHasher.Object, new MemoryCache(new MemoryCacheOptions()));

        // Act & Assert
        BadRequestException exception = await Assert.ThrowsAsync<BadRequestException>(() => service.DeleteUserAsync(1));
        Assert.Contains("Cannot delete the last admin user", exception.Message);
    }

    [Fact]
    public async Task UpdateUserAsync_UpdatesUserSuccessfully()
    {
        // Arrange
        AppDbContext context = GetInMemoryDbContext();
        var logger = new Mock<ILogger<UserService>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        var user = new User
        {
            Id = 1,
            Username = "testuser",
            PasswordHash = "oldhash",
            RoleId = 2,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var service = new UserService(context, logger.Object, passwordHasher.Object, new MemoryCache(new MemoryCacheOptions()));
        var request = new UpdateUserRequest(Role: "admin", IsEnabled: null, NewPassword: null);

        // Act
        var result = await service.UpdateUserAsync(1, request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("admin", result.Role);
    }

    [Fact]
    public async Task EnableUserAsync_EnablesDisabledUser()
    {
        // Arrange
        AppDbContext context = GetInMemoryDbContext();
        var logger = new Mock<ILogger<UserService>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        var user = new User
        {
            Id = 1,
            Username = "testuser",
            PasswordHash = "hash",
            RoleId = 2,
            IsEnabled = false,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var service = new UserService(context, logger.Object, passwordHasher.Object, new MemoryCache(new MemoryCacheOptions()));

        // Act
        UserDto result = await service.EnableUserAsync(1);

        // Assert
        Assert.True(result.IsEnabled);
    }

    [Fact]
    public async Task DisableUserAsync_DisablesEnabledUser()
    {
        // Arrange
        AppDbContext context = GetInMemoryDbContext();
        var logger = new Mock<ILogger<UserService>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        context.Users.AddRange(
            new User { Id = 1, Username = "admin", PasswordHash = "hash1", RoleId = 1, IsEnabled = true, CreatedAt = DateTime.UtcNow },
            new User { Id = 2, Username = "user1", PasswordHash = "hash2", RoleId = 2, IsEnabled = true, CreatedAt = DateTime.UtcNow }
        );
        await context.SaveChangesAsync();

        var service = new UserService(context, logger.Object, passwordHasher.Object, new MemoryCache(new MemoryCacheOptions()));

        // Act
        UserDto result = await service.DisableUserAsync(2);

        // Assert
        Assert.False(result.IsEnabled);
    }

    private static UserService NewService(AppDbContext context, IMemoryCache? cache = null)
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.HashPassword(It.IsAny<string>())).Returns<string>(p => "hashed:" + p);
        return new UserService(context, new Mock<ILogger<UserService>>().Object, hasher.Object,
            cache ?? new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task CreateUser_WithoutPassword_CreatesOidcOnlyAccount()
    {
        AppDbContext context = GetInMemoryDbContext();

        UserDto dto = await NewService(context).CreateUserAsync(
            new CreateUserRequest("bob", null, "user", Email: "bob@x.fr"));

        User stored = await context.Users.SingleAsync();
        Assert.Null(stored.PasswordHash);
        Assert.False(stored.MustChangePassword);
        Assert.Equal("bob@x.fr", stored.Email);
        Assert.False(dto.HasPassword);
        Assert.False(dto.OidcLinked);
        Assert.Equal("bob@x.fr", dto.Email);
    }

    [Fact]
    public async Task CreateUser_WithPassword_KeepsMustChangePassword()
    {
        AppDbContext context = GetInMemoryDbContext();

        UserDto dto = await NewService(context).CreateUserAsync(
            new CreateUserRequest("carol", "Password1!", "user", Email: "carol@x.fr"));

        User stored = await context.Users.SingleAsync();
        Assert.Equal("hashed:Password1!", stored.PasswordHash);
        Assert.True(stored.MustChangePassword);
        Assert.Equal("carol@x.fr", stored.Email);
        Assert.True(dto.HasPassword);
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_ThrowsConflict()
    {
        AppDbContext context = GetInMemoryDbContext();
        context.Users.Add(new User { Id = 1, Username = "alice", Email = "alice@x.fr", PasswordHash = "h", RoleId = 2 });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => NewService(context).CreateUserAsync(
            new CreateUserRequest("alice2", null, "user", Email: "Alice@X.fr")));
    }

    [Fact]
    public async Task UnlinkOidc_ClearsIdentityAndRotatesSecurityStamp()
    {
        AppDbContext context = GetInMemoryDbContext();
        context.Users.Add(new User
        {
            Id = 1, Username = "alice", Email = "alice@x.fr", PasswordHash = "h", RoleId = 2,
            OidcIssuer = "https://id.example.com", OidcSubject = "sub-1", SecurityStamp = "stamp-1"
        });
        await context.SaveChangesAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(SecurityStampCache.Key(1), "stale");

        UserDto dto = await NewService(context, cache).UnlinkOidcAsync(1);

        User stored = await context.Users.SingleAsync();
        Assert.Null(stored.OidcIssuer);
        Assert.Null(stored.OidcSubject);
        Assert.NotEqual("stamp-1", stored.SecurityStamp);
        Assert.False(cache.TryGetValue(SecurityStampCache.Key(1), out _));
        Assert.False(dto.OidcLinked);
    }

    [Fact]
    public async Task UnlinkOidc_RevokesExistingSessions()
    {
        AppDbContext context = GetInMemoryDbContext();
        context.Users.Add(new User
        {
            Id = 1, Username = "alice", Email = "alice@x.fr", PasswordHash = null, RoleId = 2,
            OidcIssuer = "https://id.example.com", OidcSubject = "sub-1"
        });
        context.Sessions.Add(new Session
        {
            UserId = 1, RefreshToken = "hash", FamilyId = "fam", DeviceInfo = "ua", IpAddress = "ip",
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await context.SaveChangesAsync();

        await NewService(context).UnlinkOidcAsync(1);

        Assert.Equal(0, await context.Sessions.CountAsync(s => s.UserId == 1));
    }

    private static void AddSsoUser(AppDbContext context, string? passwordHash, bool linked, int roleId = 2)
    {
        context.Users.Add(new User
        {
            Id = 1, Username = "alice", Email = "alice@x.fr", PasswordHash = passwordHash, RoleId = roleId,
            OidcIssuer = linked ? "https://id.example.com" : null, OidcSubject = linked ? "sub-1" : null,
            SecurityStamp = "stamp-1"
        });
        context.Sessions.Add(new Session
        {
            UserId = 1, RefreshToken = "hash", FamilyId = "fam", DeviceInfo = "ua", IpAddress = "ip",
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task UnlinkOidc_AdminWithoutPassword_IsRefused()
    {
        AppDbContext context = GetInMemoryDbContext();
        AddSsoUser(context, passwordHash: null, linked: true, roleId: 1);

        // Unlinked, an SSO-only admin could neither sign in locally nor link again.
        await Assert.ThrowsAsync<BadRequestException>(() => NewService(context).UnlinkOidcAsync(1));
        Assert.Equal("sub-1", (await context.Users.SingleAsync()).OidcSubject);
    }

    [Fact]
    public async Task UpdateUser_PromoteSsoOnlyAccountToAdmin_IsRefused()
    {
        AppDbContext context = GetInMemoryDbContext();
        AddSsoUser(context, passwordHash: null, linked: false);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            NewService(context).UpdateUserAsync(1, new UpdateUserRequest(Role: "admin")));
        Assert.Equal(2, (await context.Users.AsNoTracking().SingleAsync()).RoleId);
    }

    [Fact]
    public async Task UpdateUser_PromoteToAdminWithNewPassword_IsAllowed()
    {
        AppDbContext context = GetInMemoryDbContext();
        AddSsoUser(context, passwordHash: null, linked: false);

        await NewService(context).UpdateUserAsync(1, new UpdateUserRequest(Role: "admin", NewPassword: "Phare-Kx7q-Zt92!"));

        User stored = await context.Users.AsNoTracking().SingleAsync();
        Assert.Equal(1, stored.RoleId);
        Assert.Equal("hashed:Phare-Kx7q-Zt92!", stored.PasswordHash);
    }

    [Fact]
    public async Task UpdateUser_PromoteLinkedAccountToAdmin_RemovesSsoLinkAndSessions()
    {
        AppDbContext context = GetInMemoryDbContext();
        // The link may come from an email match: an admin must re-link from an open session.
        AddSsoUser(context, passwordHash: "h", linked: true);

        UserDto dto = await NewService(context).UpdateUserAsync(1, new UpdateUserRequest(Role: "Admin"));

        User stored = await context.Users.AsNoTracking().SingleAsync();
        Assert.Equal(1, stored.RoleId);
        Assert.Null(stored.OidcIssuer);
        Assert.Null(stored.OidcSubject);
        Assert.False(dto.OidcLinked);
        Assert.NotEqual("stamp-1", stored.SecurityStamp);
        Assert.Equal(0, await context.Sessions.CountAsync());
    }

    [Fact]
    public async Task UpdateUser_OtherChangesOnLinkedAdmin_KeepSsoLink()
    {
        AppDbContext context = GetInMemoryDbContext();
        AddSsoUser(context, passwordHash: "h", linked: true, roleId: 1);

        await NewService(context).UpdateUserAsync(1, new UpdateUserRequest(Role: "admin", Email: "new@x.fr"));

        Assert.Equal("sub-1", (await context.Users.AsNoTracking().SingleAsync()).OidcSubject);
    }

    [Fact]
    public async Task UnlinkOidc_UnknownUser_ThrowsNotFound()
    {
        AppDbContext context = GetInMemoryDbContext();
        await Assert.ThrowsAsync<NotFoundException>(() => NewService(context).UnlinkOidcAsync(42));
    }

    [Fact]
    public async Task UserDto_ReportsOidcLinkedAndHasPassword()
    {
        AppDbContext context = GetInMemoryDbContext();
        context.Users.Add(new User { Id = 1, Username = "alice", PasswordHash = null, RoleId = 2, OidcSubject = "s", OidcIssuer = "i" });
        await context.SaveChangesAsync();

        UserDto? dto = await NewService(context).GetUserByIdAsync(1);

        Assert.True(dto!.OidcLinked);
        Assert.False(dto.HasPassword);
    }
}
