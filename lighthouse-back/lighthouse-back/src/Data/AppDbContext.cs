using Microsoft.EntityFrameworkCore;
using Lighthouse.Models;

namespace Lighthouse.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<Session> Sessions { get; set; } = null!;
    public DbSet<AppSetting> AppSettings { get; set; } = null!;
    public DbSet<Operation> Operations { get; set; } = null!;
    public DbSet<UserGroup> UserGroups { get; set; } = null!;
    public DbSet<UserGroupMembership> UserGroupMemberships { get; set; } = null!;
    public DbSet<ResourcePermission> ResourcePermissions { get; set; } = null!;
    public DbSet<PasswordResetToken> PasswordResetTokens { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Username).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.HasIndex(e => new { e.OidcIssuer, e.OidcSubject })
                .IsUnique()
                .HasFilter("\"OidcSubject\" IS NOT NULL");
            // Configure Role relationship instead of trying to map the Role object as a scalar
            entity.Property(e => e.RoleId).IsRequired();
            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Role configuration
        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Permissions).IsRequired();
        });

        // Session configuration
        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RefreshToken).IsUnique();
            entity.HasIndex(e => e.FamilyId);
            entity.HasOne(e => e.User)
                .WithMany(u => u.Sessions)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppSetting configuration
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Key).IsUnique();
            entity.Property(e => e.Key).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Value).IsRequired();
        });

        // Operation configuration
        modelBuilder.Entity<Operation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OperationId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.StartedAt);
            entity.Property(e => e.OperationId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // UserGroup configuration
        modelBuilder.Entity<UserGroup>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        // UserGroupMembership configuration
        modelBuilder.Entity<UserGroupMembership>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.UserGroupId }).IsUnique();
            entity.HasOne(e => e.User)
                .WithMany(u => u.UserGroupMemberships)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.UserGroup)
                .WithMany(ug => ug.UserGroupMemberships)
                .HasForeignKey(e => e.UserGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ResourcePermission configuration
        modelBuilder.Entity<ResourcePermission>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Index for efficient permission lookups
            entity.HasIndex(e => new { e.ResourceType, e.ResourceName });
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.UserGroupId);
            entity.Property(e => e.ResourceType).IsRequired();
            entity.Property(e => e.ResourceName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Permissions).IsRequired();
            // Either UserId or UserGroupId must be set, but not both
            // This constraint will be validated at application level
            entity.HasOne(e => e.User)
                .WithMany(u => u.ResourcePermissions)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.UserGroup)
                .WithMany(ug => ug.ResourcePermissions)
                .HasForeignKey(e => e.UserGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PasswordResetToken configuration
        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.IsUsed);
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.IpAddress).IsRequired().HasMaxLength(50);
            entity.HasOne(e => e.User)
                .WithMany(u => u.PasswordResetTokens)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Seed initial data
        SeedData(modelBuilder);
    }

    private void SeedData(ModelBuilder modelBuilder)
    {
        // Use a fixed date for seeding to avoid non-deterministic model changes
        DateTime seedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Seed default roles
        modelBuilder.Entity<Role>().HasData(
            new Role
            {
                Id = 1,
                Name = "admin",
                Permissions = "[\"all\"]",
                Description = "Administrator with full access",
                CreatedAt = seedDate
            },
            new Role
            {
                Id = 2,
                Name = "user",
                Permissions = "[\"containers:read\",\"containers:write\",\"compose:read\"]",
                Description = "Regular user with limited access",
                CreatedAt = seedDate
            }
        );

        // Seed default admin user (password: adminadmin)
        // BCrypt hash for "adminadmin" with cost factor 12
        modelBuilder.Entity<User>().HasData(
            new User
            {
                Id = 1,
                Username = "admin",
                Email = null,
                PasswordHash = "$2a$12$KWzphWJ1oNVd2iDLsJPQIu/j3xeEjYHMeF8meG1EU2x84DzPzL51u",
                RoleId = 1, // admin role
                IsEnabled = true,
                MustChangePassword = true,
                MustAddEmail = true,
                // Fixed value so the seed stays deterministic across migrations.
                SecurityStamp = "00000000000000000000000000000001",
                CreatedAt = seedDate
            }
        );
    }
}
