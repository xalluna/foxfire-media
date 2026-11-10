using Foxfire.Server.Data;
using Foxfire.Server.Videos;
using Foxfire.Server.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Storage;

public sealed record OrphanEntry(Guid VideoId, string Kind, long Bytes);

public sealed record ReconcileReport(
    DateTimeOffset At,
    IReadOnlyList<OrphanEntry> Orphans,
    IReadOnlyList<string> VideosMissingFiles,
    int SizesCorrected)
{
    public long OrphanBytes => Orphans.Sum(o => o.Bytes);
}

/// <summary>
/// Compares disk with the database:
/// <list type="bullet">
/// <item>files with no video row are reported as orphans (and can be deleted on request);</item>
/// <item>Ready videos whose stream files are gone become Failed;</item>
/// <item>recorded sizes are corrected.</item>
/// </list>
/// Runs hourly and on demand from the Storage page.
/// </summary>
public sealed partial class StorageReconciler(
    IServiceScopeFactory scopes,
    IMediaStorage storage,
    TimeProvider time,
    Microsoft.Extensions.Options.IOptions<StorageOptions> options,
    ILogger<StorageReconciler> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReconcileReport? Last { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer rejects periods beyond ~49 days; keep any configured value in a safe range.
        var interval = TimeSpan.FromTicks(Math.Clamp(options.Value.ReconcileInterval.Ticks, TimeSpan.FromMinutes(1).Ticks, TimeSpan.FromDays(30).Ticks));
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    public async Task<ReconcileReport> RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<WebhookEvents>();
            var now = time.GetUtcNow();

            var videos = await db.Set<Video>()
                .Where(v => v.Status != VideoStatus.Deleting)
                .Select(v => new { v.Id, v.PublicId, v.Status, v.StorageBytes, v.SourceBytes })
                .ToDictionaryAsync(v => v.Id, ct);

            var orphans = new List<OrphanEntry>();
            foreach (var id in storage.ListPublishedVideoIds().Where(id => !videos.ContainsKey(id)))
            {
                orphans.Add(new OrphanEntry(id, "output", storage.Measure(id).OutputBytes));
            }

            foreach (var id in storage.ListSourceVideoIds().Where(id => !videos.ContainsKey(id)))
            {
                orphans.Add(new OrphanEntry(id, "source", storage.Measure(id).SourceBytes));
            }

            var missing = new List<string>();
            var corrected = 0;
            foreach (var video in videos.Values.Where(v => v.Status == VideoStatus.Ready))
            {
                if (storage.GetAssetPath(video.Id, "index.m3u8") is null)
                {
                    const string message = "The stream files are missing from storage. Re-process (if the original is kept) or re-upload.";
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    var failed = await db.Set<Video>().Where(v => v.Id == video.Id && v.Status == VideoStatus.Ready)
                        .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VideoStatus.Failed).SetProperty(v => v.ErrorMessage, message).SetProperty(v => v.UpdatedAt, now), ct);
                    if (failed == 1)
                    {
                        await events.RaiseAsync(await db.Set<Video>().AsNoTracking().SingleAsync(v => v.Id == video.Id, ct), WebhookEventTypes.VideoFailed, ct);
                        await db.SaveChangesAsync(ct);
                        missing.Add(video.PublicId);
                    }

                    await tx.CommitAsync(ct);
                    continue;
                }

                var footprint = storage.Measure(video.Id);
                if (footprint.Total != video.StorageBytes || footprint.SourceBytes != video.SourceBytes)
                {
                    corrected += await db.Set<Video>().Where(v => v.Id == video.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(v => v.StorageBytes, footprint.Total).SetProperty(v => v.SourceBytes, footprint.SourceBytes), ct);
                }
            }

            events.Wake();
            Last = new ReconcileReport(now, orphans, missing, corrected);
            if (orphans.Count > 0 || missing.Count > 0)
            {
                LogFindings(logger, orphans.Count, missing.Count);
            }

            return Last;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes files that belong to no video (re-checked against the database first).</summary>
    public async Task<int> DeleteOrphansAsync(CancellationToken ct)
    {
        var report = await RunAsync(ct);
        foreach (var id in report.Orphans.Select(o => o.VideoId).Distinct())
        {
            storage.Delete(id);
        }

        await RunAsync(ct);
        return report.Orphans.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage check found {Orphans} orphaned item(s) and {Missing} video(s) with missing files")]
    private static partial void LogFindings(ILogger logger, int orphans, int missing);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage reconciliation failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
