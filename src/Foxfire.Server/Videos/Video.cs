using Foxfire.Server.Applications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foxfire.Server.Videos;

public enum VideoStatus
{
    /// <summary>An upload token has been minted; the client has not started uploading.</summary>
    AwaitingUpload,

    /// <summary>The upload token was consumed and bytes are arriving.</summary>
    Uploading,

    /// <summary>The upload was aborted, rejected or went idle. A new token is required.</summary>
    UploadFailed,

    /// <summary>The upload token expired without being used. A new token is required.</summary>
    UploadExpired,

    Queued,
    Processing,
    Ready,

    /// <summary>Processing failed permanently. Admins can retry from the kept source.</summary>
    Failed,

    /// <summary>Files are being removed; hidden from every API.</summary>
    Deleting,
}

public enum ProcessingMode
{
    /// <summary>Stream copy into HLS (source already H.264 + AAC).</summary>
    Remux,

    /// <summary>Video copied, audio re-encoded to AAC.</summary>
    AudioTranscode,

    /// <summary>Video (and audio as needed) re-encoded.</summary>
    Transcode,
}

public sealed class Video
{
    public Guid Id { get; set; }

    /// <summary>11-character random id used in every API and URL. The internal <see cref="Id"/> is never exposed.</summary>
    public required string PublicId { get; set; }

    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }

    /// <summary>The owning application's own identifier for this video (unique per application).</summary>
    public string? ExternalId { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? OriginalFileName { get; set; }

    public VideoStatus Status { get; set; }
    public ProcessingMode? ProcessingMode { get; set; }
    public string? ErrorMessage { get; set; }

    // Upload grant (see Uploads). Only the current grant id can start an upload.
    public Guid? UploadGrantId { get; set; }
    public DateTimeOffset? UploadExpiresAt { get; set; }
    public DateTimeOffset? UploadStartedAt { get; set; }
    public DateTimeOffset? UploadLastActivityAt { get; set; }
    public string? TusFileId { get; set; }
    public long DeclaredSizeBytes { get; set; }

    // Media facts, filled by processing.
    public long SourceBytes { get; set; }
    public long StorageBytes { get; set; }
    public long? DurationMs { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }

    /// <summary>"app" or "admin:{username}".</summary>
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>Postgres xmin; guards concurrent state transitions.</summary>
    public uint Version { get; set; }
}

internal sealed class VideoEntityConfiguration : IEntityTypeConfiguration<Video>
{
    public void Configure(EntityTypeBuilder<Video> builder)
    {
        builder.ToTable("videos");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.PublicId).HasMaxLength(VideoIds.PublicIdLength).IsFixedLength();
        builder.HasIndex(v => v.PublicId).IsUnique();
        builder.HasOne(v => v.Application).WithMany().HasForeignKey(v => v.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(v => v.ExternalId).HasMaxLength(VideoRules.MaxExternalIdLength);
        builder.HasIndex(v => new { v.ApplicationId, v.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        builder.HasIndex(v => new { v.ApplicationId, v.CreatedAt });
        builder.HasIndex(v => v.Status);
        builder.HasIndex(v => v.TusFileId).IsUnique().HasFilter("tus_file_id IS NOT NULL");
        builder.Property(v => v.Title).HasMaxLength(VideoRules.MaxTitleLength);
        builder.Property(v => v.Description).HasMaxLength(VideoRules.MaxDescriptionLength);
        builder.Property(v => v.OriginalFileName).HasMaxLength(255);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(v => v.ProcessingMode).HasConversion<string>().HasMaxLength(24);
        builder.Property(v => v.ErrorMessage).HasMaxLength(2000);
        builder.Property(v => v.TusFileId).HasMaxLength(64);
        builder.Property(v => v.VideoCodec).HasMaxLength(32);
        builder.Property(v => v.AudioCodec).HasMaxLength(32);
        builder.Property(v => v.CreatedBy).HasMaxLength(100);
        builder.Property(v => v.Version).IsRowVersion();
    }
}
