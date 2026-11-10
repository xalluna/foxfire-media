using Microsoft.AspNetCore.Authentication;

namespace Foxfire.Server.Auth;

public static class AuthPolicies
{
    public const string Application = "Application";
}

public static class AuthServiceExtensions
{
    public static IServiceCollection AddFoxfireAuth(this IServiceCollection services)
    {
        services.AddSingleton<FailedAuthLimiter>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Application, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireClaim(FoxfireClaims.ApplicationId));

        return services;
    }
}
