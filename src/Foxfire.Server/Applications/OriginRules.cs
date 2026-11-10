namespace Foxfire.Server.Applications;

public static class OriginRules
{
    /// <summary>
    /// Normalizes an origin to <c>scheme://host[:port]</c> (lowercase, default ports dropped).
    /// Returns null for anything that is not an exact http(s) origin.
    /// </summary>
    public static string? Normalize(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Contains('*'))
        {
            return null;
        }

        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.IdnHost}".ToLowerInvariant()
            : $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}".ToLowerInvariant();
    }
}
