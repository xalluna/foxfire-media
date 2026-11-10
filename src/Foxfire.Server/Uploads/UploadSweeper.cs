using Foxfire.Server.Data;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Uploads;

/// <summary>
/// Periodically enforces upload lifetimes:
/// <list type="bullet">
/// <item>unused grants past their start window become UploadExpired;</item>
/// <item>uploads idle too long (no request and no bytes written) or running past the hard cap fail;</item>
/// <item>failed/expired videos never re-minted are deleted after the retention period.</item>
/// </list>
/// </summary>
public sealed partial class UploadSweeper(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<UploadOptions> options,
    ILogger<UploadSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(logger, ex);
            }
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var failures = scope.ServiceProvider.GetRequiredService<UploadFailures>();
        var deleter = scope.ServiceProvider.GetRequiredService<VideoDeleter>();
        var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
        var settings = options.Value;
        var now = time.GetUtcNow();

        // A single conditional statement, so a grant consumed at this instant is never expired by mistake.
        await db.Set<Video>()
            .Where(v => v.Status == VideoStatus.AwaitingUpload && v.UploadExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, VideoStatus.UploadExpired)
                .SetProperty(v => v.UpdatedAt, now), ct);

        var uploading = await db.Set<Video>().Where(v => v.Status == VideoStatus.Uploading).ToListAsync(ct);
        foreach (var video in uploading)
        {
            var lastActivity = video.UploadLastActivityAt ?? video.UploadStartedAt ?? video.UpdatedAt;
            if (video.TusFileId is { } fileId)
            {
                var data = new FileInfo(Path.Combine(storage.UploadsDirectory, fileId));
                if (data.Exists && data.LastWriteTimeUtc > lastActivity.UtcDateTime)
                {
                    lastActivity = new DateTimeOffset(data.LastWriteTimeUtc, TimeSpan.Zero);
                }
            }

            if (now - (video.UploadStartedAt ?? now) > settings.MaxDuration)
            {
                await failures.FailAsync(video, "The upload took longer than the maximum allowed duration.", ct);
            }
            else if (now - lastActivity > settings.IdleTimeout)
            {
                await failures.FailAsync(video, "The upload stalled with no progress and was abandoned.", ct);
            }
        }

        var cutoff = now - settings.AbandonedRetention;
        var abandoned = await db.Set<Video>()
            .Where(v => (v.Status == VideoStatus.UploadFailed || v.Status == VideoStatus.UploadExpired) && v.UpdatedAt < cutoff)
            .ToListAsync(ct);
        foreach (var video in abandoned)
        {
            try
            {
                await deleter.DeleteAsync(video, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(video).State = EntityState.Detached; // re-minted meanwhile; keep it
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Upload sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}
