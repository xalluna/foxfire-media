using Foxfire.Server.Security;
using Foxfire.Server.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Net.Http.Headers;

namespace Foxfire.Server.Streaming;

public sealed record PlaybackRefreshResponse(string Token, string ManifestUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// Token-gated HLS delivery: <c>/stream/{playbackToken}/{index.m3u8|init.mp4|seg_NNNNN.m4s|poster.jpg}</c>.
/// The video is identified only by the signed token, never by the URL, and the stored playlist uses
/// relative URIs, so segments inherit the token automatically. Every failure is the same bare 404.
/// </summary>
public static class StreamEndpoints
{
    public const string Prefix = "/stream";

    public static IEndpointRouteBuilder MapStreaming(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Prefix + "/{token}/{asset}", ServeAsync).ExcludeFromDescription();
        endpoints.MapPost(Prefix + "/{token}/refresh", RefreshAsync).ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>The one response for any denied or missing stream request.</summary>
    internal static IResult Denied(HttpContext context)
    {
        // An empty body (no ProblemDetails, whose traceId differs per request) keeps every denial identical.
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }

        ApplyCommonHeaders(context.Response);
        context.Response.Headers.CacheControl = "no-store";
        return Results.StatusCode(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ServeAsync(
        string token, string asset, HttpContext context, TokenService tokens, VideoAccess access, IMediaStorage storage, TimeProvider time, CancellationToken ct)
    {
        if (!IsSameOriginOrUnmarked(context.Request) ||
            !LocalMediaStorage.IsAllowedAssetName(asset) ||
            !tokens.TryValidate(TokenPurpose.Playback, token, out var claims) ||
            await access.AuthorizeAsync(claims, ct) is null ||
            storage.GetAssetPath(claims.VideoId, asset) is not { } path)
        {
            return Denied(context);
        }

        var response = context.Response;
        ApplyCommonHeaders(response);
        var remaining = claims.ExpiresAt - time.GetUtcNow();
        response.Headers.CacheControl = asset == "index.m3u8"
            ? "no-store"
            : $"private, max-age={(int)Math.Clamp(remaining.TotalSeconds, 0, 3600)}";

        return Results.File(path, ContentType(asset), enableRangeProcessing: false);
    }

    /// <summary>
    /// Slides the playback session forward (same session start, new expiry). <c>?mode=native</c> issues a
    /// token that lasts for the whole video, for players that cannot swap tokens mid-stream.
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        string token, string? mode, HttpContext context, TokenService tokens, VideoAccess access, PlaybackTokens playback, CancellationToken ct)
    {
        if (!IsSameOriginOrUnmarked(context.Request) ||
            !tokens.TryValidate(TokenPurpose.Playback, token, out var claims) ||
            playback.SessionExpired(claims) ||
            await access.AuthorizeAsync(claims, ct) is not { } info)
        {
            return Denied(context);
        }

        var (next, expires) = playback.Issue(info.VideoId, info.ApplicationId, info.TokenEpoch, claims.SessionStart, info.DurationMs, native: mode == "native");
        ApplyCommonHeaders(context.Response);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PlaybackRefreshResponse(next, $"{Prefix}/{next}/index.m3u8", expires));
    }

    /// <summary>
    /// Browsers label every request with Sec-Fetch-Site. Stream requests come from our own embed
    /// page, so anything labelled cross-site (another page's &lt;video&gt;, fetch or hotlink) is refused.
    /// Requests without the header (older clients, native players) fall back to token checks alone.
    /// </summary>
    private static bool IsSameOriginOrUnmarked(HttpRequest request) =>
        !request.Headers.TryGetValue("Sec-Fetch-Site", out var site) || site == "same-origin" || site == "none";

    private static void ApplyCommonHeaders(HttpResponse response)
    {
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static string ContentType(string asset) => asset switch
    {
        "index.m3u8" => "application/vnd.apple.mpegurl",
        "poster.jpg" => "image/jpeg",
        _ => "video/mp4",
    };
}
