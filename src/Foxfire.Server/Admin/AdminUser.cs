using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foxfire.Server.Admin;

public sealed class AdminUser
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }

    /// <summary>ASP.NET Core Identity v3 hash (PBKDF2-HMAC-SHA512).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Changes on password change; sessions carrying an old stamp are rejected.</summary>
    public Guid SecurityStamp { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }

    /// <summary>Created from Admin__BootstrapUsername / Admin__BootstrapPassword.</summary>
    public bool IsBootstrap { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public static string Normalize(string username) => username.Trim().ToUpperInvariant();
}

internal sealed class AdminUserEntityConfiguration : IEntityTypeConfiguration<AdminUser>
{
    public void Configure(EntityTypeBuilder<AdminUser> builder)
    {
        builder.ToTable("admin_users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Username).HasMaxLength(64);
        builder.Property(u => u.NormalizedUsername).HasMaxLength(64);
        builder.HasIndex(u => u.NormalizedUsername).IsUnique();
        builder.Property(u => u.PasswordHash).HasMaxLength(256);
    }
}
