using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Foxfire.Server.Streaming;

public sealed record VideoAccessInfo(Guid VideoId, Guid ApplicationId, VideoStatus Status, ApplicationStatus ApplicationStatus, int TokenEpoch, long? DurationMs);

/// <summary>
/// Hot-path lookup used by every segment request: is this token's video still owned by the token's
/// application, ready, and not revoked? Cached briefly; deletes and revocations evict the entry.
/// </summary>
public sealed class VideoAccess(IServiceScopeFactory scopes, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    public async Task<VideoAccessInfo?> GetAsync(Guid videoId, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(videoId), out VideoAccessInfo? cached))
        {
            return cached;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var info = await db.Set<Video>().AsNoTracking()
            .Where(v => v.Id == videoId)
            .Select(v => new VideoAccessInfo(v.Id, v.ApplicationId, v.Status, v.Application!.Status, v.Application.TokenEpoch, v.DurationMs))
            .SingleOrDefaultAsync(ct);

        cache.Set(Key(videoId), info, CacheFor);
        return info;
    }

    /// <summary>True when the token's claims still grant access to a ready video.</summary>
    public async Task<VideoAccessInfo?> AuthorizeAsync(TokenClaims claims, CancellationToken ct)
    {
        var info = await GetAsync(claims.VideoId, ct);
        return info is { Status: VideoStatus.Ready, ApplicationStatus: ApplicationStatus.Active } &&
               info.ApplicationId == claims.ApplicationId &&
               info.TokenEpoch == claims.Epoch
            ? info
            : null;
    }

    public void Evict(Guid videoId) => cache.Remove(Key(videoId));

    private static string Key(Guid videoId) => "video-access:" + videoId.ToString("N");
}

internal sealed class VideoAccessDeletionHook(VideoAccess access) : IVideoDeletionHook
{
    public Task OnDeletingAsync(Video video, CancellationToken ct)
    {
        access.Evict(video.Id);
        return Task.CompletedTask;
    }
}
