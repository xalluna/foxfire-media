using System.Net;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using tusdotnet;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;

namespace Foxfire.Server.Uploads;

/// <summary>
/// The resumable upload endpoint (tus 1.0). Authorization is a one-time upload token in
/// <c>Authorization: Bearer</c>:
/// <list type="bullet">
/// <item>Creating an upload consumes the token atomically (exactly one creation can win, and only
/// within the token's 15-minute start window).</item>
/// <item>After that the same token may only continue (HEAD/PATCH) or abort (DELETE) the one file it
/// created, while the upload is still active.</item>
/// </list>
/// </summary>
public static class TusUploads
{
    private const string ClaimsItem = "foxfire.upload.claims";

    public static IServiceCollection AddFoxfireUploads(this IServiceCollection services)
    {
        services.AddOptions<UploadOptions>().BindConfiguration(UploadOptions.Section);
        services.AddSingleton<PublicUrls>();
        services.AddSingleton<DiskSpaceGuard>();
        services.AddScoped<UploadGrants>();
        services.AddScoped<UploadFailures>();
        services.AddSingleton(sp => new TusDiskStore(sp.GetRequiredService<IMediaStorage>().UploadsDirectory, deletePartialFilesOnConcat: true));
        services.AddScoped<IVideoDeletionHook, UploadDeletionHook>();
        services.AddSingleton<UploadSweeper>();
        services.AddHostedService(sp => sp.GetRequiredService<UploadSweeper>());
        return services;
    }

    public static IEndpointConventionBuilder MapTusUploads(this IEndpointRouteBuilder endpoints)
    {
        var store = endpoints.ServiceProvider.GetRequiredService<TusDiskStore>();
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<UploadOptions>>().Value;
        var configuration = new DefaultTusConfiguration
        {
            Store = store,
            MaxAllowedUploadSizeInBytesLong = options.MaxBytes,
            AllowedExtensions = TusExtensions.All.Except(TusExtensions.Concatenation, TusExtensions.CreationDeferLength, TusExtensions.Expiration),
            Events = new Events
            {
                OnAuthorizeAsync = AuthorizeAsync,
                OnBeforeCreateAsync = BeforeCreateAsync,
                OnCreateCompleteAsync = CreateCompleteAsync,
                OnFileCompleteAsync = FileCompleteAsync,
                OnDeleteCompleteAsync = DeleteCompleteAsync,
            },
        };

        return endpoints.MapTus(UploadGrants.TusPath, _ => Task.FromResult(configuration));
    }

    /// <summary>Uploads are large; lift Kestrel's default request body limit for the tus endpoint only.</summary>
    public static IApplicationBuilder UseUnlimitedBodyForUploads(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(UploadGrants.TusPath) &&
                context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = null;
            }

            return next(context);
        });

    private static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
    }

    private static async Task AuthorizeAsync(AuthorizeContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        var tokens = services.GetRequiredService<TokenService>();
        var token = BearerToken(ctx.HttpContext);

        switch (ctx.Intent)
        {
            case IntentType.GetOptions:
                return;

            case IntentType.CreateFile:
                if (!tokens.TryValidate(TokenPurpose.Upload, token, out var createClaims))
                {
                    ctx.FailRequest(HttpStatusCode.Unauthorized, "Upload token is invalid, expired, or already used.");
                    return;
                }

                ctx.HttpContext.Items[ClaimsItem] = createClaims;
                return;

            case IntentType.WriteFile or IntentType.GetFileInfo or IntentType.DeleteFile:
                if (!tokens.TryValidate(TokenPurpose.Upload, token, ignoreExpiry: true, out var claims) ||
                    !await IsActiveUploadAsync(services, claims, ctx.FileId, touch: ctx.Intent == IntentType.WriteFile, ctx.CancellationToken))
                {
                    ctx.FailRequest(HttpStatusCode.NotFound);
                }

                return;

            default:
                ctx.FailRequest(HttpStatusCode.BadRequest, "Unsupported upload operation.");
                return;
        }
    }

    private static async Task<bool> IsActiveUploadAsync(IServiceProvider services, TokenClaims claims, string fileId, bool touch, CancellationToken ct)
    {
        var db = services.GetRequiredService<FoxfireDbContext>();
        var time = services.GetRequiredService<TimeProvider>();
        var options = services.GetRequiredService<IOptions<UploadOptions>>().Value;
        var now = time.GetUtcNow();

        var video = await db.Set<Video>().Include(v => v.Application).SingleOrDefaultAsync(v => v.Id == claims.VideoId, ct);
        if (video is null ||
            video.ApplicationId != claims.ApplicationId ||
            video.UploadGrantId != claims.GrantId ||
            video.Status != VideoStatus.Uploading ||
            video.TusFileId != fileId ||
            video.Application!.Status != ApplicationStatus.Active ||
            video.Application.TokenEpoch != claims.Epoch ||
            video.UploadStartedAt is null || now - video.UploadStartedAt > options.MaxDuration)
        {
            return false;
        }

        if (touch)
        {
            video.UploadLastActivityAt = now;
            video.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return false; // failed or deleted at this very moment
            }
        }

        return true;
    }

    private static async Task BeforeCreateAsync(BeforeCreateContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        if (ctx.HttpContext.Items[ClaimsItem] is not TokenClaims claims)
        {
            ctx.FailRequest(HttpStatusCode.Unauthorized);
            return;
        }

        if (ctx.UploadLengthIsDeferred)
        {
            ctx.FailRequest(HttpStatusCode.BadRequest, "Upload-Length is required.");
            return;
        }

        var options = services.GetRequiredService<IOptions<UploadOptions>>().Value;
        if (ctx.UploadLength > claims.MaxBytes || ctx.UploadLength > options.MaxBytes)
        {
            ctx.FailRequest(HttpStatusCode.RequestEntityTooLarge, "Upload-Length exceeds the size this token allows.");
            return;
        }

        if (!services.GetRequiredService<DiskSpaceGuard>().HasRoomFor(ctx.UploadLength))
        {
            ctx.FailRequest(HttpStatusCode.InsufficientStorage, "Not enough free disk space to accept this upload.");
            return;
        }

        var db = services.GetRequiredService<FoxfireDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var appValid = await db.Set<Application>().AnyAsync(
            a => a.Id == claims.ApplicationId && a.Status == ApplicationStatus.Active && a.TokenEpoch == claims.Epoch, ctx.CancellationToken);

        // The single atomic statement that consumes the grant: only one concurrent creation can match.
        var consumed = appValid && await db.Set<Video>()
            .Where(v => v.Id == claims.VideoId &&
                        v.ApplicationId == claims.ApplicationId &&
                        v.UploadGrantId == claims.GrantId &&
                        v.Status == VideoStatus.AwaitingUpload &&
                        v.UploadStartedAt == null &&
                        v.UploadExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, VideoStatus.Uploading)
                .SetProperty(v => v.UploadStartedAt, now)
                .SetProperty(v => v.UploadLastActivityAt, now)
                .SetProperty(v => v.UpdatedAt, now), ctx.CancellationToken) == 1;

        if (!consumed)
        {
            ctx.FailRequest(HttpStatusCode.Unauthorized, "Upload token is invalid, expired, or already used.");
        }
    }

    private static async Task CreateCompleteAsync(CreateCompleteContext ctx)
    {
        if (ctx.HttpContext.Items[ClaimsItem] is not TokenClaims claims)
        {
            return;
        }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<FoxfireDbContext>();
        await db.Set<Video>()
            .Where(v => v.Id == claims.VideoId && v.Status == VideoStatus.Uploading && v.TusFileId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.TusFileId, ctx.FileId), ctx.CancellationToken);
    }

    private static async Task FileCompleteAsync(FileCompleteContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        var db = services.GetRequiredService<FoxfireDbContext>();
        var storage = services.GetRequiredService<IMediaStorage>();
        var time = services.GetRequiredService<TimeProvider>();

        var video = await db.Set<Video>().SingleOrDefaultAsync(v => v.TusFileId == ctx.FileId && v.Status == VideoStatus.Uploading, ctx.CancellationToken);
        if (video is null)
        {
            return;
        }

        var extension = Path.GetExtension(video.OriginalFileName ?? "");
        var source = storage.AdoptSource(video.Id, Path.Combine(storage.UploadsDirectory, ctx.FileId), extension);
        await ((ITusTerminationStore)ctx.Store).DeleteFileAsync(ctx.FileId, ctx.CancellationToken); // tus bookkeeping files

        var now = time.GetUtcNow();
        video.Status = VideoStatus.Queued;
        video.TusFileId = null;
        video.SourceBytes = new FileInfo(source).Length;
        video.StorageBytes = video.SourceBytes;
        video.UploadLastActivityAt = now;
        video.UpdatedAt = now;
        await db.SaveChangesAsync(ctx.CancellationToken);
    }

    private static async Task DeleteCompleteAsync(DeleteCompleteContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        var db = services.GetRequiredService<FoxfireDbContext>();
        var video = await db.Set<Video>().SingleOrDefaultAsync(v => v.TusFileId == ctx.FileId, ctx.CancellationToken);
        if (video is not null)
        {
            await services.GetRequiredService<UploadFailures>().FailAsync(video, "The upload was cancelled by the client.", ctx.CancellationToken);
        }
    }
}

/// <summary>Marks an upload failed: partial bytes are deleted and the token stays consumed.</summary>
public sealed class UploadFailures(FoxfireDbContext db, TusDiskStore store, TimeProvider time)
{
    /// <returns>
    /// False when the row changed concurrently (e.g. the client just sent more bytes); nothing is touched then.
    /// The status is committed first (guarded by the row version) and only then are the partial bytes removed.
    /// </returns>
    public async Task<bool> FailAsync(Video video, string reason, CancellationToken ct)
    {
        var fileId = video.TusFileId;
        video.Status = VideoStatus.UploadFailed;
        video.ErrorMessage = reason;
        video.TusFileId = null;
        video.UpdatedAt = time.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(video).State = EntityState.Detached;
            return false;
        }

        if (fileId is not null)
        {
            await store.DeleteFileAsync(fileId, ct);
        }

        return true;
    }
}

internal sealed class UploadDeletionHook(TusDiskStore store) : IVideoDeletionHook
{
    public async Task OnFilesRemovingAsync(Video video, CancellationToken ct)
    {
        if (video.TusFileId is { } fileId)
        {
            await store.DeleteFileAsync(fileId, ct);
        }
    }
}
