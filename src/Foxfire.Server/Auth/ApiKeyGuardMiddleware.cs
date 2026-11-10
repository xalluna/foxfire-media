namespace Foxfire.Server.Auth;

/// <summary>
/// Runs before authentication on the app API: refuses API keys passed in the query string
/// (they would end up in proxy logs and browser history) and throttles IPs with repeated failures.
/// </summary>
internal sealed class ApiKeyGuardMiddleware(RequestDelegate next, FailedAuthLimiter limiter)
{
    private static readonly string[] KeyLikeParameters = ["api_key", "apikey", "api-key", "x-api-key", "key", "token"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        foreach (var name in context.Request.Query.Keys)
        {
            if (KeyLikeParameters.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Credentials are not accepted in the query string. Send the API key in the X-Api-Key header.")
                    .ExecuteAsync(context);
                return;
            }
        }

        if (limiter.IsBlocked(context))
        {
            await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed authentication attempts.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
