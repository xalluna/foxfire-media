using System.Collections.Concurrent;
using System.Threading.Channels;
using Foxfire.Server.Data;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Processing;

/// <summary>
/// The database is the queue; this class adds jobs, wakes the worker, and tracks running jobs so a
/// deleted video's ffmpeg process can be stopped.
/// </summary>
public sealed class ProcessingQueue(IOptions<ProcessingOptions> options, TimeProvider time)
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>
    /// Adds a queued job for the video (caller saves the context). The video must have a kept source.
    /// Call <see cref="Wake"/> after the transaction commits.
    /// </summary>
    public ProcessingJob Enqueue(FoxfireDbContext db, Video video, string reason)
    {
        var now = time.GetUtcNow();
        video.Status = VideoStatus.Queued;
        video.ErrorMessage = null;
        video.UpdatedAt = now;
        var job = new ProcessingJob
        {
            Id = Guid.CreateVersion7(),
            VideoId = video.Id,
            Status = JobStatus.Queued,
            MaxAttempts = options.Value.MaxAttempts,
            CreatedAt = now,
            AvailableAt = now,
            Reason = reason,
        };
        db.Add(job);
        return job;
    }

    /// <summary>True when the video already has a queued or running job.</summary>
    public static Task<bool> HasActiveJobAsync(FoxfireDbContext db, Guid videoId, CancellationToken ct) =>
        db.Set<ProcessingJob>().AnyAsync(j => j.VideoId == videoId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);

    public void Wake() => _wake.Writer.TryWrite(true);

    internal ValueTask<bool> WaitForWakeAsync(CancellationToken ct) => _wake.Reader.WaitToReadAsync(ct);

    internal void DrainWake()
    {
        while (_wake.Reader.TryRead(out _))
        {
        }
    }

    internal void Track(Guid videoId, CancellationTokenSource cts) => _running[videoId] = cts;

    internal void Untrack(Guid videoId) => _running.TryRemove(videoId, out _);

    /// <summary>Stops the running job for a video, if any (used when the video is deleted).</summary>
    public void Cancel(Guid videoId)
    {
        if (_running.TryGetValue(videoId, out var cts))
        {
            cts.Cancel();
        }
    }
}

internal sealed class ProcessingDeletionHook(ProcessingQueue queue) : IVideoDeletionHook
{
    public Task OnFilesRemovingAsync(Video video, CancellationToken ct)
    {
        queue.Cancel(video.Id);
        return Task.CompletedTask;
    }
}
