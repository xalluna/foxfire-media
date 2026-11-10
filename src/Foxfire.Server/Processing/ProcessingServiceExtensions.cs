using Foxfire.Server.Videos;

namespace Foxfire.Server.Processing;

public static class ProcessingServiceExtensions
{
    public static IServiceCollection AddFoxfireProcessing(this IServiceCollection services)
    {
        services.AddOptions<ProcessingOptions>().BindConfiguration(ProcessingOptions.Section);
        services.AddSingleton<ProcessingQueue>();
        services.AddSingleton<ProcessingWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessingWorker>());
        services.AddScoped<IVideoDeletionHook, ProcessingDeletionHook>();
        return services;
    }
}
