using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foxfire.Server.Webhooks;

public enum DeliveryStatus
{
    Pending,
    Succeeded,

    /// <summary>Gave up after every retry, or the application no longer has a webhook.</summary>
    Dead,
}

/// <summary>
/// Transactional outbox row: written in the same transaction as the state change it announces,
/// then delivered (and retried) by <see cref="WebhookDispatcher"/>.
/// </summary>
public sealed class WebhookDelivery
{
    public Guid Id { get; set; }

    /// <summary>Stable across retries; receivers de-duplicate on it.</summary>
    public required string EventId { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>Internal video id (no foreign key: the video may be gone by the time a delete event is sent).</summary>
    public Guid? VideoId { get; set; }

    public required string Type { get; set; }

    /// <summary>The exact JSON body that is sent and signed.</summary>
    public required string Payload { get; set; }

    public DeliveryStatus Status { get; set; }
    public int Attempt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

internal sealed class WebhookDeliveryEntityConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.EventId).HasMaxLength(40);
        builder.HasIndex(d => d.EventId).IsUnique();
        builder.Property(d => d.Type).HasMaxLength(40);
        builder.Property(d => d.Payload).HasColumnType("jsonb");
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.LastError).HasMaxLength(500);
        builder.HasIndex(d => new { d.Status, d.NextAttemptAt });
        builder.HasIndex(d => new { d.ApplicationId, d.CreatedAt });
        builder.HasIndex(d => new { d.VideoId, d.CreatedAt });
    }
}
