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
        services.AddSingleton<ApiKeyRegistry>();
        services.AddScoped<ApplicationSync>();

        return services;
    }

    public static async Task SyncApplicationsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationSync>().RunAsync(ct);
    }
}
