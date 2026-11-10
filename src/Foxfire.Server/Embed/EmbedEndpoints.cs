using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Foxfire.Server.Applications;
using Foxfire.Server.Auth;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Foxfire.Server.Streaming;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Foxfire.Server.Embed;

public sealed class EmbedOptions
{
    public const string Section = "Embed";

    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MinLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(24);
}

public sealed record CreateEmbedRequest(int? TtlSeconds);

public sealed record EmbedResponse(string EmbedUrl, string? PosterUrl, DateTimeOffset ExpiresAt);

/// <summary>Boot data for the embed player (serialized into the page; never contains an API key).</summary>
public sealed record EmbedBoot(
    string Title,
    string ManifestUrl,
    string RefreshUrl,
    string PosterUrl,
    long? DurationMs,
    DateTimeOffset ExpiresAt,
    bool Autoplay,
    bool Muted,
    double Start);

/// <summary>
/// Embedding: an application's backend mints a short-lived signed URL for one of its own videos, and
/// <c>GET /embed/{token}</c> serves the player with CSP <c>frame-ancestors</c> limited to that
/// application's origins, so another site can neither forge a URL nor frame a copied one.
/// </summary>
public static class EmbedEndpoints
{
    private static readonly JsonSerializerOptions BootJson = new(JsonSerializerDefaults.Web)
    {
        // The default encoder escapes <, >, & and quotes, so the JSON cannot close the <script> element.
        Encoder = JavaScriptEncoder.Default,
    };

    public static RouteGroupBuilder MapAppEmbeds(this RouteGroupBuilder api)
    {
        api.MapPost("/videos/{publicId}/embed", CreateAsync);
        return api;
    }

    public static IEndpointRouteBuilder MapEmbedPages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/embed/{token}", PageAsync).ExcludeFromDescription();
        endpoints.MapGet("/poster/{token}", PosterAsync).ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        string publicId,
        CreateEmbedRequest? request,
        ClaimsPrincipal user,
        FoxfireDbContext db,
        TokenService tokens,
        PublicUrls urls,
        TimeProvider time,
        IOptions<EmbedOptions> options,
        CancellationToken ct)
    {
        var video = await db.Set<Video>().AsNoTracking().OwnedBy(user).Include(v => v.Application)
            .SingleOrDefaultAsync(v => v.PublicId == publicId, ct);
        if (video is null)
        {
            return AppVideoApi.NotFound();
        }

        var app = video.Application!;
        if (app.AllowedOrigins.Count == 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Declare AllowedOrigins for this application before embedding (the player may only be framed by those origins).");
        }

        var settings = options.Value;
        var lifetime = request?.TtlSeconds is { } seconds
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, settings.MinLifetime.TotalSeconds, settings.MaxLifetime.TotalSeconds))
            : settings.DefaultLifetime;
        var expires = time.GetUtcNow() + lifetime;
        var claims = new TokenClaims(video.Id, app.Id, app.TokenEpoch, expires);

        var embed = tokens.Issue(TokenPurpose.Embed, claims);
        var poster = video.Status == VideoStatus.Ready ? urls.Absolute($"/poster/{tokens.Issue(TokenPurpose.Poster, claims)}") : null;
        return Results.Ok(new EmbedResponse(urls.Absolute($"/embed/{embed}"), poster, expires));
    }

    private static async Task<IResult> PageAsync(
        string token,
        bool? autoplay,
        bool? muted,
        double? t,
        HttpContext context,
        TokenService tokens,
        PlaybackTokens playback,
        FoxfireDbContext db,
        EmbedAssets assets,
        TimeProvider time,
        CancellationToken ct)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";

        if (!tokens.TryValidate(TokenPurpose.Embed, token, out var claims))
        {
            return Unavailable(response);
        }

        var video = await db.Set<Video>().AsNoTracking().Include(v => v.Application)
            .SingleOrDefaultAsync(v => v.Id == claims.VideoId, ct);
        var app = video?.Application;
        if (video is null || app is null ||
            video.ApplicationId != claims.ApplicationId ||
            app.Status != ApplicationStatus.Active ||
            app.TokenEpoch != claims.Epoch ||
            video.Status is VideoStatus.Deleting ||
            app.AllowedOrigins.Count == 0)
        {
            return Unavailable(response);
        }

        response.Headers.ContentSecurityPolicy = PlayerPolicy(app.AllowedOrigins);

        if (video.Status != VideoStatus.Ready)
        {
            var failed = video.Status is VideoStatus.Failed or VideoStatus.UploadFailed or VideoStatus.UploadExpired;
            return Html(Shell(video.Title, assets: null, body: failed
                ? "<p class=\"notice\">This video could not be processed.</p>"
                : "<p class=\"notice\">This video is still processing. Check back soon.</p>", refreshSeconds: failed ? null : 15), 200);
        }

        var (playbackToken, expires) = playback.Issue(video.Id, app.Id, app.TokenEpoch, time.GetUtcNow(), video.DurationMs, native: false);
        var boot = new EmbedBoot(
            video.Title,
            $"{StreamEndpoints.Prefix}/{playbackToken}/index.m3u8",
            $"{StreamEndpoints.Prefix}/{playbackToken}/refresh",
            $"{StreamEndpoints.Prefix}/{playbackToken}/poster.jpg",
            video.DurationMs,
            expires,
            autoplay ?? false,
            muted ?? false,
            Math.Max(0, t ?? 0));

        var bootJson = JsonSerializer.Serialize(boot, BootJson);
        return Html(Shell(video.Title, assets, $"<div id=\"player\"></div><script type=\"application/json\" id=\"boot\">{bootJson}</script>", null), 200);
    }

    /// <summary>Poster images for an app's own pages (thumbnails). Cross-origin readable, still token-bound.</summary>
    private static async Task<IResult> PosterAsync(
        string token, HttpContext context, TokenService tokens, VideoAccess access, Storage.IMediaStorage storage, CancellationToken ct)
    {
        context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (!tokens.TryValidate(TokenPurpose.Poster, token, out var claims) ||
            await access.AuthorizeAsync(claims, ct) is null ||
            storage.GetAssetPath(claims.VideoId, "poster.jpg") is not { } path)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        context.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.File(path, "image/jpeg", enableRangeProcessing: false);
    }

    /// <summary>The player page may only be framed by the owning application's declared origins.</summary>
    public static string PlayerPolicy(IEnumerable<string> frameAncestors) =>
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; " +
        "connect-src 'self'; worker-src 'self' blob:; base-uri 'none'; form-action 'none'; " +
        "frame-ancestors " + string.Join(' ', frameAncestors);

    private static IResult Unavailable(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        return Html(Shell("Video unavailable", assets: null, "<p class=\"notice\">This video is unavailable.</p>", null), StatusCodes.Status404NotFound);
    }

    private static IResult Html(string html, int status) => Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);

    private static string Shell(string title, EmbedAssets? assets, string body, int? refreshSeconds)
    {
        var page = new StringBuilder();
        page.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">")
            .Append("<meta name=\"robots\" content=\"noindex\">");
        if (refreshSeconds is { } seconds)
        {
            page.Append("<meta http-equiv=\"refresh\" content=\"").Append(seconds).Append("\">");
        }

        page.Append("<title>").Append(HtmlEncoder.Default.Encode(title)).Append("</title>");
        foreach (var style in assets?.Styles ?? [])
        {
            page.Append("<link rel=\"stylesheet\" href=\"").Append(HtmlEncoder.Default.Encode(style)).Append("\">");
        }

        page.Append("</head><body>").Append(body);
        if (assets?.Script is { } script)
        {
            page.Append("<script type=\"module\" src=\"").Append(HtmlEncoder.Default.Encode(script)).Append("\"></script>");
        }

        page.Append("</body></html>");
        return page.ToString();
    }
}
