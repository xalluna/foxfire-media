using System.Security.Claims;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Processing;
using Foxfire.Server.Storage;
using Foxfire.Server.Uploads;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Admin;

public sealed record AdminCreateUploadRequest(string? Application, string? Title, string? Filename, long SizeBytes, string? Description);

public sealed record AdminJobRow(
    string VideoId,
    string VideoTitle,
    string Application,
    string Status,
    int Attempt,
    int MaxAttempts,
    double ProgressPercent,
    string? Stage,
    string? LastError,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record AdminApplicationOption(string Name, string DisplayName, string Status);

/// <summary>Admin uploads (minted the same one-time way as app uploads) and the processing queue view.</summary>
public static class AdminUploadApi
{
    public static RouteGroupBuilder MapAdminUploads(this RouteGroupBuilder admin)
    {
        admin.MapGet("/application-options", async (FoxfireDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Set<Application>().AsNoTracking().OrderBy(a => a.Name)
                .Select(a => new AdminApplicationOption(a.Name, a.DisplayName, a.Status.ToString())).ToListAsync(ct)));

        admin.MapPost("/uploads", CreateAsync);
        admin.MapGet("/jobs", JobsAsync);
        return admin;
    }

    private static async Task<IResult> CreateAsync(
        AdminCreateUploadRequest request,
        ClaimsPrincipal user,
        FoxfireDbContext db,
        UploadGrants grants,
        DiskSpaceGuard disk,
        IOptions<UploadOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        var normalized = Application.Normalize(request.Application ?? "");
        var app = await db.Set<Application>().SingleOrDefaultAsync(a => a.NormalizedName == normalized && a.Status == ApplicationStatus.Active, ct);
        if (app is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["application"] = ["Choose an active application."] });
        }

        return await UploadApi.CreateForAsync(
            app,
            new CreateUploadRequest(request.Title, request.Filename, request.SizeBytes, ExternalId: null, request.Description),
            user.AuditName(),
            db, grants, disk, options, time, ct);
    }

    /// <summary>Active jobs first, then the most recent finished ones.</summary>
    private static async Task<IResult> JobsAsync(FoxfireDbContext db, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await db.Set<ProcessingJob>().AsNoTracking()
            .Where(j => j.Video!.Status != VideoStatus.Deleting)
            .OrderBy(j => j.Status == JobStatus.Running ? 0 : j.Status == JobStatus.Queued ? 1 : 2)
            .ThenByDescending(j => j.CreatedAt)
            .Take(take)
            .Select(j => new AdminJobRow(
                j.Video!.PublicId,
                j.Video.Title,
                j.Video.Application!.Name,
                j.Status.ToString(),
                j.Attempt,
                j.MaxAttempts,
                j.ProgressPercent,
                j.Stage,
                j.LastError,
                j.Reason,
                j.CreatedAt,
                j.StartedAt,
                j.FinishedAt))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }
}
