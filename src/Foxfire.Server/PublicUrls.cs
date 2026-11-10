using Foxfire.Server.Security;

namespace Foxfire.Server;

/// <summary>
/// Builds absolute URLs from the configured <c>PublicBaseUrl</c>. The request's Host header is never
/// used, so a spoofed Host cannot redirect uploads or embeds elsewhere.
/// </summary>
public sealed class PublicUrls
{
    public const string Key = "PublicBaseUrl";

    public PublicUrls(IConfiguration configuration)
    {
        var raw = configuration[Key];
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new FoxfireConfigurationException(
                [$"{Key} must be set to the server's public origin, e.g. https://media.example.com (no path)."]);
        }

        BaseUri = uri;
        Origin = uri.GetLeftPart(UriPartial.Authority);
    }

    public Uri BaseUri { get; }

    /// <summary>scheme://host[:port] with no trailing slash.</summary>
    public string Origin { get; }

    public string Absolute(string path) => Origin + (path.StartsWith('/') ? path : "/" + path);

    public bool IsHttps => BaseUri.Scheme == Uri.UriSchemeHttps;
}

public static class PublicSchemeExtensions
{
    /// <summary>
    /// The server is reached through <c>PublicBaseUrl</c>. When that is https, TLS is terminated in
    /// front of the server (reverse proxy), so requests are treated as https: cookies get the Secure
    /// flag and antiforgery works without trusting forwarded headers.
    /// </summary>
    public static IApplicationBuilder UsePublicScheme(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetRequiredService<PublicUrls>().IsHttps)
        {
            app.Use((context, next) =>
            {
                context.Request.Scheme = Uri.UriSchemeHttps;
                return next(context);
            });
        }

        return app;
    }
}
