using System.Buffers.Text;
using System.Security.Claims;
using Foxfire.Server.Auth;
using Foxfire.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Videos;

public static class AppVideoApi
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>The single "not found" every lookup returns, whether the video is missing, deleted or another app's.</summary>
    public static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    /// <summary>Videos owned by the calling application and visible to it (Deleting rows are hidden).</summary>
    public static IQueryable<Video> OwnedBy(this IQueryable<Video> videos, ClaimsPrincipal user)
    {
        var appId = user.GetApplicationId();
        return videos.Where(v => v.ApplicationId == appId && v.Status != VideoStatus.Deleting);
    }

    public static RouteGroupBuilder MapAppVideos(this RouteGroupBuilder api)
    {
        var videos = api.MapGroup("/videos");

        videos.MapGet("/", ListAsync);

        videos.MapGet("/{publicId}", async (string publicId, ClaimsPrincipal user, FoxfireDbContext db, CancellationToken ct) =>
            await db.Set<Video>().AsNoTracking().OwnedBy(user).SingleOrDefaultAsync(v => v.PublicId == publicId, ct) is { } video
                ? Results.Ok(VideoResponse.From(video))
                : NotFound());

        videos.MapGet("/by-external-id/{externalId}", async (string externalId, ClaimsPrincipal user, FoxfireDbContext db, CancellationToken ct) =>
            await db.Set<Video>().AsNoTracking().OwnedBy(user).SingleOrDefaultAsync(v => v.ExternalId == externalId, ct) is { } video
                ? Results.Ok(VideoResponse.From(video))
                : NotFound());

        videos.MapPatch("/{publicId}", UpdateAsync);

        videos.MapDelete("/{publicId}", async (string publicId, ClaimsPrincipal user, FoxfireDbContext db, VideoDeleter deleter, CancellationToken ct) =>
        {
            var video = await db.Set<Video>().OwnedBy(user).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
            if (video is null)
            {
                return NotFound();
            }

            await deleter.DeleteAsync(video, ct);
            return Results.NoContent();
        });

        return api;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, FoxfireDbContext db, string? status, string? cursor, int? limit, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.Set<Video>().AsNoTracking().OwnedBy(user);

        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<VideoStatus>(status, ignoreCase: true, out var parsed) || parsed == VideoStatus.Deleting)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Unknown status."] });
            }

            query = query.Where(v => v.Status == parsed);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryDecodeCursor(cursor, out var after))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Invalid cursor."] });
            }

            query = query.Where(v => v.Id < after);
        }

        // Ids are UUIDv7 (time-ordered), so id order is creation order and gives a stable keyset cursor.
        var rows = await query
            .OrderByDescending(v => v.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var next = rows.Count > pageSize ? EncodeCursor(rows[pageSize - 1]) : null;
        return Results.Ok(new VideoPage([.. rows.Take(pageSize).Select(VideoResponse.From)], next));
    }

    private static async Task<IResult> UpdateAsync(
        string publicId, UpdateVideoRequest request, ClaimsPrincipal user, FoxfireDbContext db, TimeProvider time, CancellationToken ct)
    {
        var video = await db.Set<Video>().OwnedBy(user).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return NotFound();
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

        if (VideoRules.ValidateExternalId(request.ExternalId) is { } externalIdError)
        {
            errors["externalId"] = [externalIdError];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (request.ExternalId is not null && request.ExternalId != video.ExternalId &&
            await db.Set<Video>().AnyAsync(v => v.ApplicationId == video.ApplicationId && v.ExternalId == request.ExternalId, ct))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Another video already uses this externalId.");
        }

        video.Title = request.Title?.Trim() ?? video.Title;
        video.Description = request.Description ?? video.Description;
        video.ExternalId = request.ExternalId ?? video.ExternalId;
        video.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return Results.Ok(VideoResponse.From(video));
    }

    private static string EncodeCursor(Video last)
    {
        Span<byte> buffer = stackalloc byte[16];
        last.Id.TryWriteBytes(buffer, bigEndian: true, out _);
        return Base64Url.EncodeToString(buffer);
    }

    private static bool TryDecodeCursor(string cursor, out Guid id)
    {
        id = default;
        Span<byte> buffer = stackalloc byte[16];
        // TryDecodeFromChars throws (rather than returning false) on illegal characters, so validate first.
        if (cursor.Length > 32 || !Base64Url.IsValid(cursor) ||
            !Base64Url.TryDecodeFromChars(cursor, buffer, out var written) || written != 16)
        {
            return false;
        }

        id = new Guid(buffer, bigEndian: true);
        return true;
    }
}
