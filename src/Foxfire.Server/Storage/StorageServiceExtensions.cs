using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Storage;

public static class StorageServiceExtensions
{
    public static IServiceCollection AddFoxfireStorage(this IServiceCollection services)
    {
        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section);
        services.AddSingleton<IMediaStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var contentRoot = sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
            return new LocalMediaStorage(Path.IsPathRooted(options.Root) ? options.Root : Path.Combine(contentRoot, options.Root));
        });
        services.AddSingleton<IStorageStats, DriveStorageStats>();
        services.AddSingleton<StorageReconciler>();
        services.AddHostedService(sp => sp.GetRequiredService<StorageReconciler>());
        services.AddHealthChecks().AddCheck<StorageWritableHealthCheck>("storage", tags: ["ready"]);

        return services;
    }
}

internal sealed class StorageWritableHealthCheck(IMediaStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var probe = Path.Combine(storage.Root, $".probe-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(probe, [1], cancellationToken);
            File.Delete(probe);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("Media storage is not writable.");
        }
    }
}
