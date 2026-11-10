using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Media;

public static class MediaServiceExtensions
{
    public static IServiceCollection AddFoxfireMedia(this IServiceCollection services)
    {
        services.AddOptions<MediaOptions>().BindConfiguration(MediaOptions.Section);
        services.AddSingleton<IMediaProcessor, FfmpegMediaProcessor>();
        services.AddHealthChecks().AddCheck<FfmpegHealthCheck>("ffmpeg", tags: ["ready"]);
        return services;
    }
}

/// <summary>Ready only when ffmpeg and ffprobe can be executed (when the ffmpeg processor is in use).</summary>
internal sealed class FfmpegHealthCheck(IOptions<MediaOptions> options, IMediaProcessor processor) : IHealthCheck
{
    private volatile bool _verified;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_verified || processor is not FfmpegMediaProcessor)
        {
            return HealthCheckResult.Healthy();
        }

        try
        {
            foreach (var tool in new[] { options.Value.FfmpegPath, options.Value.FfprobePath })
            {
                var result = await ProcessRunner.RunAsync(tool, ["-version"], null, cancellationToken);
                if (result.ExitCode != 0)
                {
                    return HealthCheckResult.Unhealthy();
                }
            }

            _verified = true;
            return HealthCheckResult.Healthy();
        }
        catch (InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
