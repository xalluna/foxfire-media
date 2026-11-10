using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Foxfire.Server.Web;

/// <summary>
/// Serves the built web UI (web/ → wwwroot): hashed bundles under /assets only, and the admin SPA
/// under /admin. Nothing else in wwwroot (e.g. the Vite manifest) is reachable.
/// </summary>
public static class WebHosting
{
    public const string AdminPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; " +
        "connect-src 'self'; worker-src 'self' blob:; font-src 'self'; object-src 'none'; base-uri 'none'; " +
        "form-action 'self'; frame-ancestors 'none'";

    public static WebApplication UseWebAssets(this WebApplication app)
    {
        var assets = app.Environment.WebRootPath is { } root ? Path.Combine(root, "assets") : null;
        if (assets is not null && Directory.Exists(assets))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = "/assets",
                FileProvider = new PhysicalFileProvider(assets),
                ServeUnknownFileTypes = false,
                OnPrepareResponse = ctx =>
                {
                    var headers = ctx.Context.Response.Headers;
                    headers.CacheControl = "public, max-age=31536000, immutable"; // file names are content hashes
                    headers[HeaderNames.XContentTypeOptions] = "nosniff";
                    headers["Cross-Origin-Resource-Policy"] = "same-origin";
                },
            });
        }

        return app;
    }

    public static IEndpointRouteBuilder MapAdminUi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();
        endpoints.MapGet("/admin/{**path}", (HttpContext context, IWebHostEnvironment environment) =>
        {
            var index = environment.WebRootPath is { } root ? Path.Combine(root, "admin", "index.html") : null;
            if (index is null || !File.Exists(index))
            {
                return Results.NotFound();
            }

            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = AdminPolicy;
            headers.XFrameOptions = "DENY";
            headers.CacheControl = "no-cache";
            headers[HeaderNames.XContentTypeOptions] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            return Results.File(index, "text/html; charset=utf-8");
        }).ExcludeFromDescription();

        return endpoints;
    }
}
