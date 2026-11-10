using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Data;
using Foxfire.Server.Media;
using Foxfire.Server.Processing;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Processing;

public sealed class ProcessingTests
{
    private static async Task<(FoxfireFactory Factory, HttpClient App)> StartAsync(string? connectionString = null)
    {
        var factory = new FoxfireFactory { ConnectionString = connectionString ?? "" };
        await factory.InitializeAsync();
        var key = factory.AddApp("AppA");
        return (factory, factory.CreateAppClient(key));
    }

    private static async Task<Guid> UploadAsync(FoxfireFactory factory, HttpClient app, byte[]? bytes = null)
    {
        bytes ??= new byte[2048];
        var ticket = await TusClient.MintAsync(app, sizeBytes: bytes.Length);
        var tus = new TusClient(factory.CreateClient(), ticket.UploadToken);
        var location = await tus.CreateOrThrowAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await tus.PatchAsync(location, 0, bytes)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<Video>()
            .Where(v => v.PublicId == ticket.VideoId).Select(v => v.Id).SingleAsync();
    }

    private static async Task<List<ProcessingJob>> JobsAsync(FoxfireFactory factory, Guid videoId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<ProcessingJob>()
            .AsNoTracking().Where(j => j.VideoId == videoId).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task An_upload_is_processed_to_ready_with_output_published_and_the_original_kept()
    {
        var (factory, app) = await StartAsync();
        await using var _ = factory;
        using var __ = app;

        var videoId = await UploadAsync(factory, app);
        await factory.WaitForStatusAsync(videoId, VideoStatus.Ready);

        var video = (await factory.FindVideoAsync(videoId))!;
        var storage = factory.Services.GetRequiredService<IMediaStorage>();
        Assert.Equal(ProcessingMode.Remux, video.ProcessingMode);
        Assert.Equal(12_000, video.DurationMs);
        Assert.Equal(1280, video.Width);
        Assert.NotNull(video.ReadyAt);
        Assert.NotNull(storage.GetAssetPath(videoId, "index.m3u8"));
        Assert.NotNull(storage.GetAssetPath(videoId, "poster.jpg"));
        Assert.NotNull(storage.FindSource(videoId));
        Assert.Equal(storage.Measure(videoId).Total, video.StorageBytes);
        Assert.Equal(2048, video.SourceBytes);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(storage.Root, "work")));

        var job = Assert.Single(await JobsAsync(factory, videoId));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(100, job.ProgressPercent);

        var response = await app.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{video.PublicId}");
        Assert.Equal("Ready", response!.Status);
        Assert.Equal(720, response.Height);
    }

    [Fact]
    public async Task Transient_failures_are_retried()
    {
        var (factory, app) = await StartAsync();
        await using var _ = factory;
        using var __ = app;
        factory.Media.FailNext(new MediaProcessingException("ffmpeg crashed"));
        factory.Media.FailNext(new IOException("disk hiccup"));

        var videoId = await UploadAsync(factory, app);
        await factory.WaitForStatusAsync(videoId, VideoStatus.Ready);

        var job = Assert.Single(await JobsAsync(factory, videoId));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(3, job.Attempt);
        Assert.Equal(3, factory.Media.Calls);
    }

    [Fact]
    public async Task Persistent_failures_end_in_failed_with_a_sanitized_message()
    {
        var (factory, app) = await StartAsync();
        await using var _ = factory;
        using var __ = app;
        for (var i = 0; i < 3; i++)
        {
            factory.Media.FailNext(new MediaProcessingException("<file>: Invalid data found when processing input"));
        }

        var videoId = await UploadAsync(factory, app);
        await factory.WaitForStatusAsync(videoId, VideoStatus.Failed);

        var video = (await factory.FindVideoAsync(videoId))!;
        Assert.Contains("Invalid data", video.ErrorMessage, StringComparison.Ordinal);
        var job = Assert.Single(await JobsAsync(factory, videoId));
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempt);
        Assert.NotNull(factory.Services.GetRequiredService<IMediaStorage>().FindSource(videoId)); // kept for an admin retry

        var response = await app.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{video.PublicId}");
        Assert.Equal("Failed", response!.Status);
        Assert.Contains("Invalid data", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_media_fails_immediately_without_retries()
    {
        var (factory, app) = await StartAsync();
        await using var _ = factory;
        using var __ = app;
        factory.Media.FailNext(new MediaRejectedException("Unsupported container format 'hls'."));

        var videoId = await UploadAsync(factory, app);
        await factory.WaitForStatusAsync(videoId, VideoStatus.Failed);

        Assert.Equal(1, factory.Media.Calls);
        Assert.Equal(1, Assert.Single(await JobsAsync(factory, videoId)).Attempt);
    }

    [Fact]
    public async Task Deleting_a_video_mid_processing_cancels_the_job_and_leaves_nothing_behind()
    {
        var (factory, app) = await StartAsync();
        await using var _ = factory;
        using var __ = app;
        factory.Media.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var videoId = await UploadAsync(factory, app);
        await factory.WaitForStatusAsync(videoId, VideoStatus.Processing);
        var publicId = (await factory.FindVideoAsync(videoId))!.PublicId;

        Assert.Equal(HttpStatusCode.NoContent, (await app.DeleteAsync($"/api/v1/videos/{publicId}")).StatusCode);
        await TestData.WaitUntilAsync(async () => (await factory.FindVideoAsync(videoId)) is null, "video row removal");

        var storage = factory.Services.GetRequiredService<IMediaStorage>();
        await TestData.WaitUntilAsync(() => Task.FromResult(!Directory.EnumerateDirectories(Path.Combine(storage.Root, "work")).Any()), "work dir cleanup");
        Assert.Null(storage.GetAssetPath(videoId, "index.m3u8"));
        Assert.Null(storage.FindSource(videoId));
        Assert.Empty(await JobsAsync(factory, videoId)); // cascade
    }

    [Fact]
    public async Task Jobs_interrupted_by_a_crash_are_resumed_after_restart()
    {
        var connectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();
        Guid videoId;
        var storageRoot = "";

        // Process 1 dies mid-job: simulate by writing a Running job leased by a worker that no longer exists.
        await using (var crashed = new FoxfireFactory { ConnectionString = connectionString })
        {
            crashed.AddApp("AppA");
            await crashed.InitializeAsync();
            var video = await crashed.SeedVideoAsync("AppA", status: VideoStatus.Processing, withSource: true);
            videoId = video.Id;
            storageRoot = crashed.StorageRoot;
            await using var scope = crashed.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
            db.Add(new ProcessingJob
            {
                Id = Guid.CreateVersion7(), VideoId = videoId, Status = JobStatus.Running, Attempt = 1, MaxAttempts = 3,
                LeaseOwner = "dead-worker", LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt = DateTimeOffset.UtcNow, AvailableAt = DateTimeOffset.UtcNow, Reason = "upload",
            });
            await db.SaveChangesAsync();
            CopyDirectory(storageRoot, storageRoot + "-kept");
        }

        // Process 2 starts on the same database and media.
        await using var restarted = new FoxfireFactory { ConnectionString = connectionString };
        restarted.Settings["Storage:Root"] = storageRoot + "-kept";
        restarted.AddApp("AppA");
        _ = restarted.Server;

        await restarted.WaitForStatusAsync(videoId, VideoStatus.Ready);
        var job = Assert.Single(await JobsAsync(restarted, videoId));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, job.Attempt);
        Directory.Delete(storageRoot + "-kept", recursive: true);
    }

    [Fact]
    public async Task A_job_that_was_interrupted_on_every_attempt_is_failed_instead_of_looping()
    {
        var connectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();
        await using var factory = new FoxfireFactory { ConnectionString = connectionString };
        factory.AddApp("AppA");
        await factory.InitializeAsync();
        var video = await factory.SeedVideoAsync("AppA", status: VideoStatus.Queued, withSource: true);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();

            // Seeding happens after this host's recovery ran, so the worker sees a stale lease instead.
            db.Add(new ProcessingJob
            {
                Id = Guid.CreateVersion7(), VideoId = video.Id, Status = JobStatus.Running, Attempt = 3, MaxAttempts = 3,
                LeaseOwner = "dead-worker", LeaseExpiresAt = factory.Time.GetUtcNow().AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow, AvailableAt = DateTimeOffset.UtcNow, Reason = "upload",
            });
            await db.SaveChangesAsync();
        }

        factory.Services.GetRequiredService<ProcessingQueue>().Wake();

        await factory.WaitForStatusAsync(video.Id, VideoStatus.Failed);
        Assert.Contains("interrupted repeatedly", (await factory.FindVideoAsync(video.Id))!.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, factory.Media.Calls);
    }

    [Fact]
    public async Task Queued_videos_without_a_job_get_one_at_startup()
    {
        var connectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();
        Guid videoId;
        string storageRoot;
        await using (var first = new FoxfireFactory { ConnectionString = connectionString })
        {
            first.AddApp("AppA");
            await first.InitializeAsync();
            first.Media.Gate = new TaskCompletionSource(); // never let this host process anything
            videoId = (await first.SeedVideoAsync("AppA", status: VideoStatus.Queued, withSource: true)).Id;
            storageRoot = first.StorageRoot;
            CopyDirectory(storageRoot, storageRoot + "-kept");
        }

        await using var second = new FoxfireFactory { ConnectionString = connectionString };
        second.Settings["Storage:Root"] = storageRoot + "-kept";
        second.AddApp("AppA");
        _ = second.Server;

        await second.WaitForStatusAsync(videoId, VideoStatus.Ready);
        Assert.Equal("recovery", Assert.Single(await JobsAsync(second, videoId)).Reason);
        Directory.Delete(storageRoot + "-kept", recursive: true);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
        }
    }
}
