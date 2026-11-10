using System.Diagnostics;

namespace Foxfire.Server.Security;

/// <summary>Rewrites request paths so bearer-style path tokens never reach the logs.</summary>
public static class RequestPathRedactor
{
    private static readonly string[] TokenPrefixes = ["embed", "stream"];

    public static string Redact(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return "/";
        }

        var segments = value.Split('/');
        // segments[0] is empty because the path starts with '/'.
        if (segments.Length > 2 && TokenPrefixes.Contains(segments[1], StringComparer.OrdinalIgnoreCase))
        {
            segments[2] = "***";
            return string.Join('/', segments);
        }

        return value;
    }
}

/// <summary>
/// The only per-request log line the server writes. It records the redacted path and never the
/// query string or headers (so no Authorization / X-Api-Key value can be logged).
/// </summary>
internal sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            LogRequest(logger, context.Request.Method, RequestPathRedactor.Redact(context.Request.Path), context.Response.StatusCode, elapsed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Method} {Path} -> {StatusCode} in {ElapsedMs:0.0}ms")]
    private static partial void LogRequest(ILogger logger, string method, string path, int statusCode, double elapsedMs);
}
