using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foxfire.Server.Applications;

public enum ApplicationStatus
{
    Active,

    /// <summary>Previously configured, no longer present in configuration. No key exists, so it cannot authenticate.</summary>
    Missing,
}

/// <summary>
/// The persisted mirror of a configured application. Deliberately has no column for an API key or
/// anything derived from one (hash, prefix, fingerprint).
/// </summary>
public sealed class Application
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public required string DisplayName { get; set; }
    public List<string> AllowedOrigins { get; set; } = [];
    public string? WebhookUrl { get; set; }
    public ApplicationStatus Status { get; set; }

    /// <summary>Bumping this revokes every outstanding embed/playback/upload token for the app.</summary>
    public int TokenEpoch { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
    public DateTimeOffset? MissingSince { get; set; }

    public static string Normalize(string name) => name.ToUpperInvariant();
}

internal sealed class ApplicationEntityConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> builder)
    {
        builder.ToTable("applications");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).HasMaxLength(32);
        builder.Property(a => a.NormalizedName).HasMaxLength(32);
        builder.HasIndex(a => a.NormalizedName).IsUnique();
        builder.Property(a => a.DisplayName).HasMaxLength(200);
        builder.Property(a => a.WebhookUrl).HasMaxLength(2048);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
    }
}
