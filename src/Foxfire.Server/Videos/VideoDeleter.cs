using Foxfire.Server.Data;
using Foxfire.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Videos;

/// <summary>
/// Two-phase delete: the row is first marked <see cref="VideoStatus.Deleting"/> (instantly hidden from
/// every API and every token), then files are removed and the row is dropped. A crash between the
/// phases is finished by <see cref="ResumePendingAsync"/> at startup.
/// </summary>
public sealed partial class VideoDeleter(
    FoxfireDbContext db,
    IMediaStorage storage,
    IEnumerable<IVideoDeletionHook> hooks,
    TimeProvider time,
    ILogger<VideoDeleter> logger)
{
    public async Task DeleteAsync(Video video, CancellationToken ct)
    {
        video.Status = VideoStatus.Deleting;
        video.UpdatedAt = time.GetUtcNow();
        foreach (var hook in hooks)
        {
            await hook.OnDeletingAsync(video, ct);
        }

        await db.SaveChangesAsync(ct);

        await RemoveAsync(video, ct);
    }

    public async Task ResumePendingAsync(CancellationToken ct)
    {
        var pending = await db.Set<Video>().Where(v => v.Status == VideoStatus.Deleting).ToListAsync(ct);
        foreach (var video in pending)
        {
            await RemoveAsync(video, ct);
        }
    }

    private async Task RemoveAsync(Video video, CancellationToken ct)
    {
        try
        {
            foreach (var hook in hooks)
            {
                await hook.OnFilesRemovingAsync(video, ct);
            }

            storage.Delete(video.Id);
            db.Remove(video);
            await db.SaveChangesAsync(ct);
        }
        catch (IOException ex)
        {
            // Row stays in Deleting (hidden); the next startup retries.
            LogRetryLater(logger, video.PublicId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove files for video {PublicId}; will retry at next startup")]
    private static partial void LogRetryLater(ILogger logger, string publicId, Exception ex);
}

/// <summary>Lets other features (uploads, processing, webhooks) react to a video being deleted.</summary>
public interface IVideoDeletionHook
{
    /// <summary>Runs inside the transaction that marks the video Deleting.</summary>
    Task OnDeletingAsync(Video video, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Runs before the files are removed.</summary>
    Task OnFilesRemovingAsync(Video video, CancellationToken ct) => Task.CompletedTask;
}
