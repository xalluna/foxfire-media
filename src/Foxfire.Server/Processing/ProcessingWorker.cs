using System.Linq.Expressions;
using Foxfire.Server.Data;
using Foxfire.Server.Media;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Foxfire.Server.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Processing;

/// <summary>
/// Claims queued jobs (FOR UPDATE SKIP LOCKED), runs the media processor into a scratch directory,
/// then atomically publishes the result. Video state changes are single conditional statements, so a
/// concurrent edit or delete is never overwritten.
/// </summary>
public sealed partial class ProcessingWorker(
    IServiceScopeFactory scopes,
    ProcessingQueue queue,
    IMediaProcessor processor,
    IMediaStorage storage,
    TimeProvider time,
    IOptions<ProcessingOptions> options,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    /// <summary>Unique per process start, so jobs leased by a previous run are recognisably stale.</summary>
    public string WorkerId { get; } = $"{Environment.ProcessId}-{Guid.NewGuid():N}";

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.Value.Concurrency)).Select(_ => RunSlotAsync(stoppingToken)));

    private async Task RunSlotAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await TryProcessNextAsync(stoppingToken))
                {
                    continue; // drain the queue before sleeping
                }

                queue.DrainWake();
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var wake = queue.WaitForWakeAsync(poll.Token).AsTask();
                var timeout = Task.Delay(options.Value.PollInterval, time, poll.Token);
                await Task.WhenAny(wake, timeout);
                await poll.CancelAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLoopError(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Claims and runs one job. Returns false when nothing was available.</summary>
    public async Task<bool> TryProcessNextAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var settings = options.Value;
        var now = time.GetUtcNow();
        var leaseUntil = now + settings.LeaseDuration;

        // A worker that died (or lost its database connection) stops renewing; its job goes back in line.
        await db.Set<ProcessingJob>()
            .Where(j => j.Status == JobStatus.Running && j.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null).SetProperty(j => j.AvailableAt, now), stoppingToken);

        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE processing_jobs
            SET status = 'Running', attempt = attempt + 1, lease_owner = {WorkerId}, lease_expires_at = {leaseUntil},
                started_at = {now}, stage = 'Starting', progress_percent = 0
            WHERE id = (
                SELECT id FROM processing_jobs
                WHERE status = 'Queued' AND available_at <= {now}
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING id AS "Value"
            """).ToListAsync(stoppingToken);
        if (claimed.Count == 0)
        {
            return false;
        }

        var job = await db.Set<ProcessingJob>().AsNoTracking().SingleAsync(j => j.Id == claimed[0], stoppingToken);
        await RunJobAsync(db, scope.ServiceProvider.GetRequiredService<WebhookEvents>(), job, stoppingToken);
        return true;
    }

    private async Task RunJobAsync(FoxfireDbContext db, WebhookEvents events, ProcessingJob job, CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var videoId = job.VideoId;

        if (job.Attempt > job.MaxAttempts)
        {
            // Every earlier attempt was interrupted (crash, OOM kill, power loss) before it could finish.
            const string message = "Processing was interrupted repeatedly and has been stopped.";
            await FinishJobAsync(db, job.Id, JobStatus.Failed, message);
            await TransitionAsync(db, events, videoId, v => v.Status == VideoStatus.Queued || v.Status == VideoStatus.Processing,
                s => s.SetProperty(v => v.Status, VideoStatus.Failed).SetProperty(v => v.ErrorMessage, message).SetProperty(v => v.UpdatedAt, time.GetUtcNow()),
                WebhookEventTypes.VideoFailed);
            return;
        }

        // Announce "processing" once per job, not on every retry.
        var started = await TransitionAsync(db, events, videoId, v => v.Status == VideoStatus.Queued || v.Status == VideoStatus.Processing,
            s => s.SetProperty(v => v.Status, VideoStatus.Processing).SetProperty(v => v.UpdatedAt, time.GetUtcNow()),
            job.Attempt == 1 ? WebhookEventTypes.VideoProcessing : null);
        var source = storage.FindSource(videoId);
        if (!started || source is null)
        {
            await FinishJobAsync(db, job.Id, JobStatus.Cancelled, !started ? "Video is no longer queued." : "The source file is missing.");
            if (started)
            {
                await SetVideoAsync(db, events, videoId, VideoStatus.Processing, VideoStatus.Failed, "The original file is missing; re-upload the video.");
            }

            return;
        }

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        queue.Track(videoId, jobCts);
        var workDir = storage.CreateWorkDirectory(videoId, job.Attempt);
        var progress = new LatestProgress();
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(job.Id, progress, heartbeatStop.Token);

        try
        {
            var facts = await processor.ProcessAsync(source, workDir, progress, jobCts.Token);
            await heartbeatStop.CancelAsync();
            await heartbeat;
            await CompleteAsync(db, events, job, workDir, facts);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: put the job back without spending an attempt.
            await db.Set<ProcessingJob>().Where(j => j.Id == job.Id && j.Status == JobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.Attempt, j => j.Attempt - 1)
                    .SetProperty(j => j.LeaseOwner, (string?)null).SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), CancellationToken.None);
            await SetVideoAsync(db, events, videoId, VideoStatus.Processing, VideoStatus.Queued, null);
        }
        catch (OperationCanceledException)
        {
            // The video was deleted mid-job; the deleter removes everything else.
            await FinishJobAsync(db, job.Id, JobStatus.Cancelled, "Cancelled.");
        }
        catch (MediaRejectedException ex)
        {
            await FinishJobAsync(db, job.Id, JobStatus.Failed, ex.Message);
            await SetVideoAsync(db, events, videoId, VideoStatus.Processing, VideoStatus.Failed, ex.Message);
            LogRejected(logger, videoId, ex.Message);
        }
        catch (Exception ex) when (ex is MediaProcessingException or IOException or InvalidOperationException)
        {
            var message = ex is MediaProcessingException ? ex.Message : "Processing failed unexpectedly.";
            LogAttemptFailed(logger, videoId, job.Attempt, ex);
            if (job.Attempt < job.MaxAttempts)
            {
                var retryAt = time.GetUtcNow() + (settings.RetryDelay * job.Attempt);
                await db.Set<ProcessingJob>().Where(j => j.Id == job.Id && j.Status == JobStatus.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.AvailableAt, retryAt)
                        .SetProperty(j => j.LastError, message).SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), CancellationToken.None);
                await SetVideoAsync(db, events, videoId, VideoStatus.Processing, VideoStatus.Queued, null);
            }
            else
            {
                await FinishJobAsync(db, job.Id, JobStatus.Failed, message);
                await SetVideoAsync(db, events, videoId, VideoStatus.Processing, VideoStatus.Failed, message);
            }
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            queue.Untrack(videoId);
            try
            {
                Directory.Delete(workDir, recursive: true); // gone already when published or when the video was deleted
            }
            catch (IOException)
            {
                // Absent, or still locked; startup recovery removes stragglers.
            }
        }
    }

    private async Task CompleteAsync(FoxfireDbContext db, WebhookEvents events, ProcessingJob job, string workDir, MediaFacts facts)
    {
        var videoId = job.VideoId;

        // Re-check right before publishing: the video may have been deleted while ffmpeg ran.
        var stillProcessing = await db.Set<Video>().AnyAsync(v => v.Id == videoId && v.Status == VideoStatus.Processing);
        if (!stillProcessing)
        {
            await FinishJobAsync(db, job.Id, JobStatus.Cancelled, "Video is no longer processing.");
            return;
        }

        storage.Publish(videoId, workDir);
        if (!options.Value.KeepSource && storage.FindSource(videoId) is { } source)
        {
            File.Delete(source);
        }

        var footprint = storage.Measure(videoId);
        var now = time.GetUtcNow();
        var updated = await TransitionAsync(db, events, videoId, v => v.Status == VideoStatus.Processing, s => s
                .SetProperty(v => v.Status, VideoStatus.Ready)
                .SetProperty(v => v.ProcessingMode, facts.Mode)
                .SetProperty(v => v.DurationMs, facts.DurationMs)
                .SetProperty(v => v.Width, facts.Width)
                .SetProperty(v => v.Height, facts.Height)
                .SetProperty(v => v.VideoCodec, facts.VideoCodec)
                .SetProperty(v => v.AudioCodec, facts.AudioCodec)
                .SetProperty(v => v.StorageBytes, footprint.Total)
                .SetProperty(v => v.SourceBytes, footprint.SourceBytes)
                .SetProperty(v => v.ErrorMessage, (string?)null)
                .SetProperty(v => v.ReadyAt, now)
                .SetProperty(v => v.UpdatedAt, now),
            WebhookEventTypes.VideoReady);

        await FinishJobAsync(db, job.Id, updated ? JobStatus.Succeeded : JobStatus.Cancelled, null, progress: 100);
        if (updated)
        {
            LogReady(logger, videoId, facts.Mode);
        }
    }

    private async Task HeartbeatAsync(Guid jobId, LatestProgress progress, CancellationToken ct)
    {
        var settings = options.Value;
        try
        {
            while (true)
            {
                await Task.Delay(settings.HeartbeatInterval, time, ct);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
                var leaseUntil = time.GetUtcNow() + settings.LeaseDuration;
                var percent = Math.Round(progress.Value, 1);
                await db.Set<ProcessingJob>().Where(j => j.Id == jobId && j.Status == JobStatus.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, leaseUntil)
                        .SetProperty(j => j.ProgressPercent, percent).SetProperty(j => j.Stage, "Encoding"), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Job finished.
        }
    }

    private async Task FinishJobAsync(FoxfireDbContext db, Guid jobId, JobStatus status, string? error, double? progress = null)
    {
        var now = time.GetUtcNow();
        await db.Set<ProcessingJob>().Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.LastError, j => error ?? j.LastError)
                .SetProperty(j => j.ProgressPercent, j => progress ?? j.ProgressPercent)
                .SetProperty(j => j.Stage, status.ToString())
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), CancellationToken.None);
    }

    private async Task SetVideoAsync(FoxfireDbContext db, WebhookEvents events, Guid videoId, VideoStatus from, VideoStatus to, string? error)
    {
        var now = time.GetUtcNow();
        await TransitionAsync(db, events, videoId, v => v.Status == from,
            s => s.SetProperty(v => v.Status, to).SetProperty(v => v.ErrorMessage, error).SetProperty(v => v.UpdatedAt, now),
            to == VideoStatus.Failed ? WebhookEventTypes.VideoFailed : null);
    }

    /// <summary>
    /// A conditional state change and (when it applied) its webhook event, committed together.
    /// </summary>
    private static async Task<bool> TransitionAsync(
        FoxfireDbContext db,
        WebhookEvents events,
        Guid videoId,
        Expression<Func<Video, bool>> when,
        Action<UpdateSettersBuilder<Video>> set,
        string? eventType)
    {
        await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var applied = await db.Set<Video>().Where(v => v.Id == videoId).Where(when).ExecuteUpdateAsync(set, CancellationToken.None) == 1;
        if (applied && eventType is not null)
        {
            var video = await db.Set<Video>().AsNoTracking().SingleAsync(v => v.Id == videoId);
            await events.RaiseAsync(video, eventType, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await tx.CommitAsync(CancellationToken.None);
        if (applied && eventType is not null)
        {
            events.Wake();
        }

        return applied;
    }

    /// <summary>
    /// Startup recovery (single instance): jobs left Running by a previous process are re-queued, or
    /// failed when out of attempts; videos left Processing return to Queued; queued videos without a
    /// job get one; stray scratch directories are removed.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<WebhookEvents>();
        var now = time.GetUtcNow();

        // Anything still Running belongs to a previous process. Re-queue it; the attempt it already spent
        // counts, so a job that crashes the server every time ends up Failed instead of looping forever.
        var stale = await db.Set<ProcessingJob>()
            .Where(j => j.Status == JobStatus.Running && j.LeaseOwner != WorkerId)
            .ToListAsync(ct);
        foreach (var job in stale)
        {
            job.Status = JobStatus.Queued;
            job.AvailableAt = now;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            await SetVideoAsync(db, events, job.VideoId, VideoStatus.Processing, VideoStatus.Queued, null);
        }

        await db.SaveChangesAsync(ct);

        var orphaned = await db.Set<Video>()
            .Where(v => (v.Status == VideoStatus.Queued || v.Status == VideoStatus.Processing) &&
                        !db.Set<ProcessingJob>().Any(j => j.VideoId == v.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)))
            .ToListAsync(ct);
        foreach (var video in orphaned)
        {
            queue.Enqueue(db, video, "recovery");
        }

        await db.SaveChangesAsync(ct);

        storage.CleanWorkDirectories(new HashSet<Guid>());
        if (stale.Count > 0 || orphaned.Count > 0)
        {
            LogRecovered(logger, stale.Count, orphaned.Count);
        }
    }

    private sealed class LatestProgress : IProgress<double>
    {
        private double _value;

        public double Value => Volatile.Read(ref _value);

        public void Report(double value) => Volatile.Write(ref _value, value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Video {VideoId} is ready ({Mode})")]
    private static partial void LogReady(ILogger logger, Guid videoId, ProcessingMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Video {VideoId} was rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, Guid videoId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing attempt {Attempt} for video {VideoId} failed")]
    private static partial void LogAttemptFailed(ILogger logger, Guid videoId, int attempt, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {StaleJobs} interrupted job(s) and {OrphanedVideos} queued video(s) without a job")]
    private static partial void LogRecovered(ILogger logger, int staleJobs, int orphanedVideos);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing loop error")]
    private static partial void LogLoopError(ILogger logger, Exception ex);
}
