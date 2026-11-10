using Foxfire.Server.Applications;

namespace Foxfire.Server.Security;

public static class SecurityServiceExtensions
{
    public static IServiceCollection AddFoxfireTokens(this IServiceCollection services)
    {
        services.AddSingleton(sp => SigningKeys.Load(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ApplicationCatalog>()));
        services.AddSingleton<TokenService>();
        return services;
    }
}
