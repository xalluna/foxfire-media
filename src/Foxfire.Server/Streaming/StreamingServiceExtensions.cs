using Foxfire.Server.Videos;

namespace Foxfire.Server.Streaming;

public static class StreamingServiceExtensions
{
    public static IServiceCollection AddFoxfireStreaming(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddOptions<StreamingOptions>().BindConfiguration(StreamingOptions.Section);
        services.AddSingleton<VideoAccess>();
        services.AddSingleton<PlaybackTokens>();
        services.AddScoped<IVideoDeletionHook, VideoAccessDeletionHook>();
        return services;
    }
}
