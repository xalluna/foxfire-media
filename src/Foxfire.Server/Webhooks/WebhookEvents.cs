using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Webhooks;

public static class WebhookEventTypes
{
    public const string UploadStarted = "upload.started";
    public const string UploadCompleted = "upload.completed";
    public const string UploadFailed = "upload.failed";
    public const string UploadExpired = "upload.expired";
    public const string VideoProcessing = "video.processing";
    public const string VideoReady = "video.ready";
    public const string VideoFailed = "video.failed";
    public const string VideoDeleted = "video.deleted";
    public const string Test = "webhook.test";
}

public sealed record WebhookVideoData(
    string VideoId,
    string? ExternalId,
    string Title,
    string Status,
    long? DurationMs,
    int? Width,
    int? Height,
    string? Error);

public sealed record WebhookEnvelope(string Id, string Type, DateTimeOffset CreatedAt, object Data);

/// <summary>
/// Records webhook events into the outbox on the caller's DbContext, so they commit (or roll back)
/// with the state change. Nothing is recorded for applications without a webhook URL.
/// Payloads never contain tokens, embed URLs or keys.
/// </summary>
public sealed class WebhookEvents(FoxfireDbContext db, WebhookSignal signal, TimeProvider time)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Adds the event for <paramref name="video"/> to the change tracker (caller saves).</summary>
    public async Task RaiseAsync(Video video, string type, CancellationToken ct, string? statusOverride = null)
    {
        var app = video.Application ?? await db.Set<Application>().AsNoTracking().SingleAsync(a => a.Id == video.ApplicationId, ct);
        if (app.WebhookUrl is null || app.Status != ApplicationStatus.Active)
        {
            return;
        }

        var data = new WebhookVideoData(
            video.PublicId,
            video.ExternalId,
            video.Title,
            statusOverride ?? video.Status.ToString(),
            video.DurationMs,
            video.Width,
            video.Height,
            type is WebhookEventTypes.UploadFailed or WebhookEventTypes.VideoFailed ? video.ErrorMessage : null);
        Add(app.Id, video.Id, type, data);
    }

    /// <summary>A delivery whose data is not a video (e.g. an admin's test event).</summary>
    public void RaiseRaw(Guid applicationId, string type, object data) => Add(applicationId, null, type, data);

    /// <summary>Raise + save + wake, for callers that changed state outside the change tracker.</summary>
    public async Task RaiseAndSaveAsync(Guid videoId, string type, CancellationToken ct)
    {
        var video = await db.Set<Video>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null)
        {
            return;
        }

        await RaiseAsync(video, type, ct);
        await db.SaveChangesAsync(ct);
        signal.Wake();
    }

    public void Wake() => signal.Wake();

    private void Add(Guid applicationId, Guid? videoId, string type, object data)
    {
        var now = time.GetUtcNow();
        var eventId = "evt_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
        var payload = JsonSerializer.Serialize(new WebhookEnvelope(eventId, type, now, data), Json);
        db.Add(new WebhookDelivery
        {
            Id = Guid.CreateVersion7(),
            EventId = eventId,
            ApplicationId = applicationId,
            VideoId = videoId,
            Type = type,
            Payload = payload,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Wake the dispatcher once this context's changes are committed.
        db.SavedChanges -= OnSaved;
        db.SavedChanges += OnSaved;
    }

    private void OnSaved(object? sender, SavedChangesEventArgs e)
    {
        if (db.Database.CurrentTransaction is null)
        {
            signal.Wake();
        }
    }
}

/// <summary>Lets the dispatcher sleep until there is something to send.</summary>
public sealed class WebhookSignal
{
    private readonly System.Threading.Channels.Channel<bool> _channel =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
        });

    public void Wake() => _channel.Writer.TryWrite(true);

    internal async Task WaitAsync(TimeSpan max, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(max);
        try
        {
            await _channel.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Poll interval elapsed.
        }
    }
}

internal sealed class WebhookDeletionHook(WebhookEvents events) : IVideoDeletionHook
{
    public Task OnDeletingAsync(Video video, CancellationToken ct) =>
        events.RaiseAsync(video, WebhookEventTypes.VideoDeleted, ct, statusOverride: "Deleted");
}
