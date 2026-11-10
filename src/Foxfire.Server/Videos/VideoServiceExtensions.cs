namespace Foxfire.Server.Videos;

public static class VideoServiceExtensions
{
    public static IServiceCollection AddFoxfireVideos(this IServiceCollection services)
    {
        services.AddScoped<VideoDeleter>();
        return services;
    }

    public static async Task ResumePendingDeletesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VideoDeleter>().ResumePendingAsync(ct);
    }
}
