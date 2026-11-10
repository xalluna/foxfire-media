using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>Direct database/storage seeding for tests that should not depend on the upload pipeline.</summary>
public static class TestData
{
    public static async Task<Guid> AppIdAsync(this FoxfireFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var normalized = Application.Normalize(name);
        return await db.Set<Application>().Where(a => a.NormalizedName == normalized).Select(a => a.Id).SingleAsync();
    }

    public static async Task<Video> SeedVideoAsync(
        this FoxfireFactory factory,
        string appName,
        VideoStatus status = VideoStatus.Ready,
        string? title = null,
        string? externalId = null,
        bool withFiles = false,
        bool withSource = false)
    {
        var appId = await factory.AppIdAsync(appName);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var now = DateTimeOffset.UtcNow;
        var video = new Video
        {
            Id = Guid.CreateVersion7(),
            PublicId = VideoIds.NewPublicId(),
            ApplicationId = appId,
            Title = title ?? "Video " + Guid.NewGuid().ToString("N")[..6],
            ExternalId = externalId,
            Status = status,
            CreatedBy = "app",
            CreatedAt = now,
            UpdatedAt = now,
            ReadyAt = status == VideoStatus.Ready ? now : null,
            DurationMs = 12_000,
            Width = 1280,
            Height = 720,
        };
        db.Add(video);
        await db.SaveChangesAsync();

        if (withFiles)
        {
            var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
            var work = storage.CreateWorkDirectory(video.Id, 1);
            await File.WriteAllTextAsync(Path.Combine(work, "index.m3u8"),
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.0,\nseg_00000.m4s\n#EXTINF:6.0,\nseg_00001.m4s\n#EXT-X-ENDLIST\n");
            await File.WriteAllBytesAsync(Path.Combine(work, "init.mp4"), new byte[64]);
            await File.WriteAllBytesAsync(Path.Combine(work, "seg_00000.m4s"), new byte[256]);
            await File.WriteAllBytesAsync(Path.Combine(work, "seg_00001.m4s"), new byte[256]);
            await File.WriteAllBytesAsync(Path.Combine(work, "poster.jpg"), new byte[32]);
            storage.Publish(video.Id, work);
        }

        if (withFiles || withSource)
        {
            var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
            var source = Path.Combine(storage.UploadsDirectory, Guid.NewGuid().ToString("N"));
            await File.WriteAllBytesAsync(source, new byte[1024]);
            storage.AdoptSource(video.Id, source, ".mp4");
        }

        return video;
    }

    /// <summary>Polls (in real time) until the condition holds; the server's own clock is fake and does not move.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    public static Task WaitForStatusAsync(this FoxfireFactory factory, Guid videoId, VideoStatus status) =>
        WaitUntilAsync(async () => (await factory.FindVideoAsync(videoId))?.Status == status, $"video to become {status}");

    public static async Task<Video?> FindVideoAsync(this FoxfireFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<Video>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == id);
    }
}
