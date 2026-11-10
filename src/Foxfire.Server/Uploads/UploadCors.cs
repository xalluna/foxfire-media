using System.Collections.Frozen;
using Foxfire.Server.Applications;
using Microsoft.Net.Http.Headers;

namespace Foxfire.Server.Uploads;

/// <summary>
/// CORS for direct browser uploads. Preflights are answered for any configured application origin
/// (the preflight carries no credentials, so the owning app is not yet known); the actual request is
/// then checked against the origins of the application that owns the upload token (see
/// <see cref="TusUploads"/>). FoxfireMedia's own origin (the admin UI) is always allowed.
/// </summary>
public sealed class UploadCors
{
    private const string AllowedMethods = "POST, HEAD, PATCH, DELETE, OPTIONS";
    private const string AllowedHeaders = "Authorization, Content-Type, Tus-Resumable, Upload-Length, Upload-Metadata, Upload-Offset, X-HTTP-Method-Override, X-Requested-With";
    private const string ExposedHeaders = "Location, Tus-Resumable, Tus-Version, Tus-Max-Size, Tus-Extension, Upload-Offset, Upload-Length, Upload-Metadata";

    private readonly FrozenSet<string> _anyAppOrigin;

    public UploadCors(ApplicationCatalog catalog, PublicUrls urls)
    {
        OwnOrigin = urls.Origin.ToLowerInvariant();
        _anyAppOrigin = catalog.Applications.SelectMany(a => a.AllowedOrigins).Append(OwnOrigin).ToFrozenSet(StringComparer.Ordinal);
    }

    public string OwnOrigin { get; }

    public static string? RequestOrigin(HttpContext context) =>
        context.Request.Headers.Origin is { Count: 1 } values ? OriginRules.Normalize(values[0]) : null;

    public bool IsKnownOrigin(string? origin) => origin is not null && _anyAppOrigin.Contains(origin);

    /// <summary>Middleware for the tus path: answers preflights and decorates actual responses.</summary>
    public async Task HandleAsync(HttpContext context, RequestDelegate next)
    {
        var hasOrigin = context.Request.Headers.ContainsKey(HeaderNames.Origin);
        var origin = RequestOrigin(context);
        var allowed = IsKnownOrigin(origin);

        if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod))
        {
            context.Response.Headers.Vary = HeaderNames.Origin;
            if (allowed)
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.AccessControlAllowMethods = AllowedMethods;
                context.Response.Headers.AccessControlAllowHeaders = AllowedHeaders;
                context.Response.Headers.AccessControlMaxAge = "600";
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (hasOrigin)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.Vary = HeaderNames.Origin;
                if (allowed)
                {
                    context.Response.Headers.AccessControlAllowOrigin = origin;
                    context.Response.Headers.AccessControlExposeHeaders = ExposedHeaders;
                }

                return Task.CompletedTask;
            });
        }

        await next(context);
    }
}
