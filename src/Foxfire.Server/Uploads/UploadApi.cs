using System.Security.Claims;
using Foxfire.Server.Applications;
using Foxfire.Server.Auth;
using Foxfire.Server.Data;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Uploads;

public sealed record CreateUploadRequest(string? Title, string? Filename, long SizeBytes, string? ExternalId, string? Description);

public sealed record RenewUploadRequest(long? SizeBytes);

public static class UploadApi
{
    public static RouteGroupBuilder MapAppUploads(this RouteGroupBuilder api)
    {
        api.MapPost("/uploads", CreateAsync);
        api.MapPost("/videos/{publicId}/upload-token", RenewAsync);
        return api;
    }

    /// <summary>Creates a video awaiting upload and mints the one-time token the app hands to its client.</summary>
    private static async Task<IResult> CreateAsync(
        CreateUploadRequest request,
        ClaimsPrincipal user,
        FoxfireDbContext db,
        UploadGrants grants,
        DiskSpaceGuard disk,
        IOptions<UploadOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        var app = await db.Set<Application>().SingleAsync(a => a.Id == user.GetApplicationId(), ct);
        return await CreateForAsync(app, request, "app", db, grants, disk, options, time, ct);
    }

    /// <summary>Shared by the app API and the admin console (which uploads on behalf of a chosen application).</summary>
    internal static async Task<IResult> CreateForAsync(
        Application app,
        CreateUploadRequest request,
        string createdBy,
        FoxfireDbContext db,
        UploadGrants grants,
        DiskSpaceGuard disk,
        IOptions<UploadOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (VideoRules.ValidateTitle(request.Title) is { } titleError)
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

        if (string.IsNullOrWhiteSpace(request.Filename) || request.Filename.Length > 255)
        {
            errors["filename"] = ["Filename is required (max 255 characters); its extension is kept for processing."];
        }

        if (SizeProblem(request.SizeBytes, options.Value) is { } sizeError)
        {
            errors["sizeBytes"] = [sizeError];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (!disk.HasRoomFor(request.SizeBytes))
        {
            return InsufficientStorage();
        }

        if (request.ExternalId is not null &&
            await db.Set<Video>().AnyAsync(v => v.ApplicationId == app.Id && v.ExternalId == request.ExternalId, ct))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "A video with this externalId already exists. Request a new token with POST /api/v1/videos/{id}/upload-token.");
        }

        var now = time.GetUtcNow();
        var video = new Video
        {
            Id = Guid.CreateVersion7(),
            PublicId = VideoIds.NewPublicId(),
            ApplicationId = app.Id,
            ExternalId = request.ExternalId,
            Title = request.Title!.Trim(),
            Description = request.Description,
            OriginalFileName = SanitizeFileName(request.Filename!),
            DeclaredSizeBytes = request.SizeBytes,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var ticket = grants.Mint(video, app);
        db.Add(video);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/videos/{video.PublicId}", ticket);
    }

    /// <summary>Mints a fresh grant for a video whose previous upload failed, expired, or never started.</summary>
    private static async Task<IResult> RenewAsync(
        string publicId,
        RenewUploadRequest? request,
        ClaimsPrincipal user,
        FoxfireDbContext db,
        UploadGrants grants,
        DiskSpaceGuard disk,
        IOptions<UploadOptions> options,
        CancellationToken ct)
    {
        var video = await db.Set<Video>().OwnedBy(user).Include(v => v.Application).SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return AppVideoApi.NotFound();
        }

        if (video.Status is not (VideoStatus.AwaitingUpload or VideoStatus.UploadFailed or VideoStatus.UploadExpired))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"A new upload token can only be issued before an upload starts or after it failed (video is {video.Status}).");
        }

        if (request?.SizeBytes is { } size)
        {
            if (SizeProblem(size, options.Value) is { } sizeError)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sizeBytes"] = [sizeError] });
            }

            video.DeclaredSizeBytes = size;
        }

        if (!disk.HasRoomFor(video.DeclaredSizeBytes))
        {
            return InsufficientStorage();
        }

        var ticket = grants.Mint(video, video.Application!);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ticket);
    }

    internal static IResult InsufficientStorage() =>
        Results.Problem(statusCode: StatusCodes.Status507InsufficientStorage, title: "Not enough free disk space to accept this upload.");

    private static string? SizeProblem(long size, UploadOptions options) =>
        size <= 0 ? "sizeBytes must be positive."
        : size > options.MaxBytes ? $"sizeBytes exceeds the maximum upload size of {options.MaxBytes} bytes."
        : null;

    private static string SanitizeFileName(string name)
    {
        var fileName = Path.GetFileName(name.Replace('\\', '/'));
        var clean = new string([.. fileName.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length == 0 ? "upload" : clean;
    }
}
