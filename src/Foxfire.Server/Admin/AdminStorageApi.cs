using Foxfire.Server.Data;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Admin;

public sealed record StorageDriveSummary(string MountPoint, string Format, long TotalBytes, long FreeBytes, long UsedBytes, double FreePercent);

public sealed record StorageAppUsage(string Application, int VideoCount, long SourceBytes, long OutputBytes);

public sealed record StorageLargestItem(string Id, string Title, string Application, long Bytes);

public sealed record StorageCheckSummary(DateTimeOffset At, int OrphanCount, long OrphanBytes, IReadOnlyList<string> VideosMissingFiles, int SizesCorrected);

public sealed record StorageSummary(
    StorageDriveSummary Drive,
    bool LowSpace,
    double LowSpaceWarningPercent,
    long VideoBytes,
    long SourceBytes,
    long OutputBytes,
    long UploadsInProgressBytes,
    long ProcessingScratchBytes,
    IReadOnlyList<StorageAppUsage> PerApplication,
    IReadOnlyList<StorageLargestItem> Largest,
    StorageCheckSummary? LastCheck);

public static class AdminStorageApi
{
    public static RouteGroupBuilder MapAdminStorage(this RouteGroupBuilder admin)
    {
        admin.MapGet("/storage", SummaryAsync);
        admin.MapPost("/storage/check", async (StorageReconciler reconciler, CancellationToken ct) => Results.Ok(Summarize(await reconciler.RunAsync(ct))));
        admin.MapPost("/storage/orphans/delete", async (StorageReconciler reconciler, CancellationToken ct) =>
        {
            await reconciler.DeleteOrphansAsync(ct);
            return Results.NoContent();
        });
        return admin;
    }

    private static async Task<IResult> SummaryAsync(
        FoxfireDbContext db, IStorageStats stats, IMediaStorage storage, StorageReconciler reconciler, IOptions<StorageOptions> options, CancellationToken ct)
    {
        var drive = stats.GetDrive();
        var freePercent = drive.TotalBytes > 0 ? Math.Round(drive.FreeBytes * 100.0 / drive.TotalBytes, 1) : 0;
        var visible = db.Set<Video>().AsNoTracking().Where(v => v.Status != VideoStatus.Deleting);

        var perApp = await visible
            .GroupBy(v => v.Application!.Name)
            .Select(g => new StorageAppUsage(g.Key, g.Count(), g.Sum(v => v.SourceBytes), g.Sum(v => v.StorageBytes - v.SourceBytes)))
            .ToListAsync(ct);

        var largest = await visible
            .OrderByDescending(v => v.StorageBytes)
            .Take(10)
            .Select(v => new StorageLargestItem(v.PublicId, v.Title, v.Application!.Name, v.StorageBytes))
            .ToListAsync(ct);

        var source = perApp.Sum(a => a.SourceBytes);
        var output = perApp.Sum(a => a.OutputBytes);
        return Results.Ok(new StorageSummary(
            new StorageDriveSummary(drive.MountPoint, drive.Format, drive.TotalBytes, drive.FreeBytes, drive.UsedBytes, freePercent),
            freePercent < options.Value.LowSpaceWarningPercent,
            options.Value.LowSpaceWarningPercent,
            source + output,
            source,
            output,
            storage.MeasureUploads(),
            storage.MeasureWork(),
            [.. perApp.OrderByDescending(a => a.SourceBytes + a.OutputBytes)],
            largest,
            reconciler.Last is { } last ? Summarize(last) : null));
    }

    private static StorageCheckSummary Summarize(ReconcileReport report) =>
        new(report.At, report.Orphans.Count, report.OrphanBytes, report.VideosMissingFiles, report.SizesCorrected);
}
