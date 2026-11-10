using System.Buffers.Text;
using System.Security.Claims;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Processing;
using Foxfire.Server.Security;
using Foxfire.Server.Storage;
using Foxfire.Server.Streaming;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Admin;

public sealed record AdminVideoItem(
    string Id,
    string Title,
    string Application,
    string Status,
    long? DurationMs,
    int? Width,
    int? Height,
    long SizeBytes,
    string? ThumbnailUrl,
    DateTimeOffset CreatedAt);

public sealed record AdminVideoPage(IReadOnlyList<AdminVideoItem> Items, string? NextCursor);

public sealed record AdminJobItem(string Status, int Attempt, int MaxAttempts, double ProgressPercent, string? Stage, string? LastError, string Reason, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

public sealed record AdminVideoDetail(
    string Id,
    string Title,
    string? Description,
    string? ExternalId,
    string Application,
    string ApplicationStatus,
    string Status,
    string? ProcessingMode,
    string? Error,
    long? DurationMs,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    long SourceBytes,
    long OutputBytes,
    bool HasSource,
    string? OriginalFileName,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    IReadOnlyList<AdminJobItem> Jobs);

public sealed record AdminUpdateVideoRequest(string? Title, string? Description);

public sealed record AdminPreview(string Title, string ManifestUrl, string RefreshUrl, string PosterUrl, DateTimeOffset ExpiresAt);

/// <summary>Admin view of every application's videos: browse, search, edit, delete, re-process and preview.</summary>
public static class AdminVideoApi
{
    public static RouteGroupBuilder MapAdminVideos(this RouteGroupBuilder admin)
    {
        var videos = admin.MapGroup("/videos");
        videos.MapGet("/", ListAsync);
        videos.MapGet("/{publicId}", DetailAsync);
        videos.MapPatch("/{publicId}", UpdateAsync);
        videos.MapDelete("/{publicId}", DeleteAsync);
        videos.MapPost("/{publicId}/reprocess", ReprocessAsync);
        videos.MapPost("/{publicId}/preview", PreviewAsync);
        return admin;
    }

    private static IQueryable<Video> Visible(FoxfireDbContext db) =>
        db.Set<Video>().Where(v => v.Status != VideoStatus.Deleting);

    private static async Task<IResult> ListAsync(
        FoxfireDbContext db, TokenService tokens, TimeProvider time,
        string? q, string? app, string? status, string? cursor, int? limit, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 48, 1, 100);
        var query = Visible(db).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var pattern = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(v => EF.Functions.ILike(v.Title, pattern, "\\") || v.PublicId == term || v.ExternalId == term);
        }

        if (!string.IsNullOrWhiteSpace(app))
        {
            var normalized = Application.Normalize(app);
            query = query.Where(v => v.Application!.NormalizedName == normalized);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<VideoStatus>(status, ignoreCase: true, out var parsed) || parsed == VideoStatus.Deleting)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Unknown status."] });
            }

            query = query.Where(v => v.Status == parsed);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            Span<byte> buffer = stackalloc byte[16];
            if (cursor.Length > 32 || !Base64Url.IsValid(cursor) || !Base64Url.TryDecodeFromChars(cursor, buffer, out var written) || written != 16)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Invalid cursor."] });
            }

            var after = new Guid(buffer, bigEndian: true);
            query = query.Where(v => v.Id < after);
        }

        var rows = await query
            .OrderByDescending(v => v.Id)
            .Take(pageSize + 1)
            .Select(v => new
            {
                v.Id,
                v.PublicId,
                v.Title,
                App = v.Application!.Name,
                AppId = v.ApplicationId,
                AppStatus = v.Application.Status,
                Epoch = v.Application.TokenEpoch,
                v.Status,
                v.DurationMs,
                v.Width,
                v.Height,
                v.StorageBytes,
                v.CreatedAt,
            })
            .ToListAsync(ct);

        var thumbnailExpiry = time.GetUtcNow().AddHours(1);
        var items = rows.Take(pageSize).Select(r => new AdminVideoItem(
            r.PublicId,
            r.Title,
            r.App,
            r.Status.ToString(),
            r.DurationMs,
            r.Width,
            r.Height,
            r.StorageBytes,
            r.Status == VideoStatus.Ready && r.AppStatus == ApplicationStatus.Active
                ? "/poster/" + tokens.Issue(TokenPurpose.Poster, new TokenClaims(r.Id, r.AppId, r.Epoch, thumbnailExpiry))
                : null,
            r.CreatedAt)).ToList();

        string? next = null;
        if (rows.Count > pageSize)
        {
            Span<byte> buffer = stackalloc byte[16];
            rows[pageSize - 1].Id.TryWriteBytes(buffer, bigEndian: true, out _);
            next = Base64Url.EncodeToString(buffer);
        }

        return Results.Ok(new AdminVideoPage(items, next));
    }

    private static async Task<IResult> DetailAsync(string publicId, FoxfireDbContext db, IMediaStorage storage, CancellationToken ct)
    {
        var video = await Visible(db).AsNoTracking().Include(v => v.Application).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return AppVideoApi.NotFound();
        }

        var jobs = await db.Set<ProcessingJob>().AsNoTracking()
            .Where(j => j.VideoId == video.Id)
            .OrderByDescending(j => j.CreatedAt)
            .Take(10)
            .Select(j => new AdminJobItem(j.Status.ToString(), j.Attempt, j.MaxAttempts, j.ProgressPercent, j.Stage, j.LastError, j.Reason, j.CreatedAt, j.FinishedAt))
            .ToListAsync(ct);

        var footprint = storage.Measure(video.Id);
        return Results.Ok(new AdminVideoDetail(
            video.PublicId,
            video.Title,
            video.Description,
            video.ExternalId,
            video.Application!.Name,
            video.Application.Status.ToString(),
            video.Status.ToString(),
            video.ProcessingMode?.ToString(),
            video.ErrorMessage,
            video.DurationMs,
            video.Width,
            video.Height,
            video.VideoCodec,
            video.AudioCodec,
            footprint.SourceBytes,
            footprint.OutputBytes,
            storage.FindSource(video.Id) is not null,
            video.OriginalFileName,
            video.CreatedBy,
            video.CreatedAt,
            video.ReadyAt,
            jobs));
    }

    private static async Task<IResult> UpdateAsync(string publicId, AdminUpdateVideoRequest request, FoxfireDbContext db, TimeProvider time, CancellationToken ct)
    {
        var video = await Visible(db).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return AppVideoApi.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        if (request.Title is not null && VideoRules.ValidateTitle(request.Title) is { } titleError)
        {
            errors["title"] = [titleError];
        }

        if (VideoRules.ValidateDescription(request.Description) is { } descriptionError)
        {
            errors["description"] = [descriptionError];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        video.Title = request.Title?.Trim() ?? video.Title;
        video.Description = request.Description ?? video.Description;
        video.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(string publicId, FoxfireDbContext db, VideoDeleter deleter, CancellationToken ct)
    {
        var video = await Visible(db).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return AppVideoApi.NotFound();
        }

        await deleter.DeleteAsync(video, ct);
        return Results.NoContent();
    }

    /// <summary>Encodes the video again from its kept original (e.g. after changing Media settings or a failure).</summary>
    private static async Task<IResult> ReprocessAsync(
        string publicId, ClaimsPrincipal user, FoxfireDbContext db, IMediaStorage storage, ProcessingQueue queue, CancellationToken ct)
    {
        var video = await Visible(db).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return AppVideoApi.NotFound();
        }

        if (video.Status is not (VideoStatus.Ready or VideoStatus.Failed))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"Only ready or failed videos can be re-processed (video is {video.Status}).");
        }

        if (storage.FindSource(video.Id) is null || await ProcessingQueue.HasActiveJobAsync(db, video.Id, ct))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The original file is no longer kept, or a job is already queued.");
        }

        queue.Enqueue(db, video, user.AuditName());
        await db.SaveChangesAsync(ct);
        queue.Wake();
        return Results.Accepted($"/api/admin/videos/{video.PublicId}");
    }

    /// <summary>A same-origin playback session for the admin's own player.</summary>
    private static async Task<IResult> PreviewAsync(string publicId, FoxfireDbContext db, PlaybackTokens playback, TimeProvider time, CancellationToken ct)
    {
        var video = await Visible(db).AsNoTracking().Include(v => v.Application).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return AppVideoApi.NotFound();
        }

        if (video.Status != VideoStatus.Ready || video.Application!.Status != ApplicationStatus.Active)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Only ready videos of active applications can be played.");
        }

        var (token, expires) = playback.Issue(video.Id, video.ApplicationId, video.Application.TokenEpoch, time.GetUtcNow(), video.DurationMs, native: false);
        return Results.Ok(new AdminPreview(
            video.Title,
            $"{StreamEndpoints.Prefix}/{token}/index.m3u8",
            $"{StreamEndpoints.Prefix}/{token}/refresh",
            $"{StreamEndpoints.Prefix}/{token}/poster.jpg",
            expires));
    }
}
