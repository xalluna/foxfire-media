namespace Foxfire.Server.Applications;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddFoxfireApplications(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            return ApplicationCatalog.Load(configuration, configuration.GetValue<bool>("Webhooks:AllowHttp"));
        });

        return services;
    }
}
