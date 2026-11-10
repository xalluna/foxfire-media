using Foxfire.Server.Applications;
using Foxfire.Server.Security;
using Foxfire.Server.Videos;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Uploads;

public sealed record UploadTicket(string VideoId, string UploadUrl, string UploadToken, DateTimeOffset ExpiresAt, long MaxBytes);

/// <summary>Mints one-time upload grants. Minting a new grant invalidates every older token for the video.</summary>
public sealed class UploadGrants(TokenService tokens, PublicUrls urls, TimeProvider time, IOptions<UploadOptions> options)
{
    public const string TusPath = "/uploads/tus";

    public UploadTicket Mint(Video video, Application app)
    {
        var now = time.GetUtcNow();
        var expires = now + options.Value.TokenLifetime;

        video.Status = VideoStatus.AwaitingUpload;
        video.UploadGrantId = Guid.NewGuid();
        video.UploadExpiresAt = expires;
        video.UploadStartedAt = null;
        video.UploadLastActivityAt = null;
        video.TusFileId = null;
        video.ErrorMessage = null;
        video.UpdatedAt = now;

        var token = tokens.Issue(TokenPurpose.Upload, new TokenClaims(video.Id, app.Id, app.TokenEpoch, expires)
        {
            GrantId = video.UploadGrantId.Value,
            MaxBytes = video.DeclaredSizeBytes,
        });

        return new UploadTicket(video.PublicId, urls.Absolute(TusPath), token, expires, video.DeclaredSizeBytes);
    }
}
