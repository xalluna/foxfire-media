using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foxfire.Server.Processing;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed class ProcessingJob
{
    public Guid Id { get; set; }
    public Guid VideoId { get; set; }
    public Video? Video { get; set; }
    public JobStatus Status { get; set; }

    /// <summary>Incremented when a worker claims the job, so a crash loop still runs out of attempts.</summary>
    public int Attempt { get; set; }

    public int MaxAttempts { get; set; }
    public string? Stage { get; set; }
    public double ProgressPercent { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Earliest time the job may be claimed (retry backoff).</summary>
    public DateTimeOffset AvailableAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>"upload", "admin:{name}" (re-process) or "recovery".</summary>
    public required string Reason { get; set; }
}

internal sealed class ProcessingJobEntityConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        builder.ToTable("processing_jobs");
        builder.HasKey(j => j.Id);
        builder.HasOne(j => j.Video).WithMany().HasForeignKey(j => j.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.Stage).HasMaxLength(32);
        builder.Property(j => j.LeaseOwner).HasMaxLength(64);
        builder.Property(j => j.LastError).HasMaxLength(2000);
        builder.Property(j => j.Reason).HasMaxLength(120);
        builder.HasIndex(j => new { j.Status, j.AvailableAt, j.CreatedAt });

        // At most one queued-or-running job per video.
        builder.HasIndex(j => j.VideoId).IsUnique().HasFilter("status IN ('Queued', 'Running')").HasDatabaseName("ix_processing_jobs_one_active_per_video");
        builder.HasIndex(j => j.VideoId).HasDatabaseName("ix_processing_jobs_video_id");
    }
}
