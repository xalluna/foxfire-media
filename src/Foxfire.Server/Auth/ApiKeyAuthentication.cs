using System.Security.Claims;
using System.Text.Encodings.Web;
using Foxfire.Server.Applications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Auth;

public static class FoxfireClaims
{
    public const string ApplicationId = "foxfire:app_id";
    public const string ApplicationName = "foxfire:app_name";

    public static Guid GetApplicationId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ApplicationId) ?? throw new InvalidOperationException("Not an application principal."));
}

/// <summary>Authenticates applications by the <c>X-Api-Key</c> header. Keys are never accepted anywhere else.</summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeyRegistry registry,
    FailedAuthLimiter limiter)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var entry = values.Count == 1 ? registry.Find(values[0] ?? "") : null;
        if (entry is null)
        {
            limiter.RecordFailure(Context);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(FoxfireClaims.ApplicationId, entry.ApplicationId.ToString()),
                new Claim(FoxfireClaims.ApplicationName, entry.ApplicationName),
            ],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Results.Problem(statusCode: 401, title: "A valid X-Api-Key header is required.").ExecuteAsync(Context);
    }
}
