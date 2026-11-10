using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Admin;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Admin;

public sealed class AdminStorageTests
{
    private static async Task<(FoxfireFactory Factory, AdminSession Admin)> StartAsync()
    {
        var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        factory.AddApp("AppA");
        factory.AddApp("AppB");
        var password = factory.AddBootstrapAdmin("root");
        return (factory, await AdminSession.SignInAsync(factory, "root", password));
    }

    [Fact]
    public async Task The_report_shows_the_drive_and_usage_per_application()
    {
        var (factory, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = admin;
        factory.Disk.TotalBytes = 4_000_000_000_000;
        factory.Disk.FreeBytes = 1_000_000_000_000;
        var big = await factory.SeedVideoAsync("AppA", title: "Big one", withFiles: true);
        await factory.SeedVideoAsync("AppB", withFiles: true);
        await admin.Client.PostAsync("/api/admin/storage/check", null); // records real sizes

        var summary = await admin.Client.GetFromJsonAsync<StorageSummary>("/api/admin/storage");

        Assert.Equal(4_000_000_000_000, summary!.Drive.TotalBytes);
        Assert.Equal(1_000_000_000_000, summary.Drive.FreeBytes);
        Assert.Equal(3_000_000_000_000, summary.Drive.UsedBytes);
        Assert.Equal(25, summary.Drive.FreePercent);
        Assert.False(summary.LowSpace);
        Assert.Equal(2, summary.PerApplication.Count);
        var appA = summary.PerApplication.Single(a => a.Application == "AppA");
        Assert.Equal(1024, appA.SourceBytes);
        Assert.True(appA.OutputBytes > 0);
        Assert.Equal(summary.SourceBytes + summary.OutputBytes, summary.VideoBytes);
        Assert.Contains(summary.Largest, l => l.Id == big.PublicId);
    }

    [Fact]
    public async Task Low_free_space_is_flagged()
    {
        var (factory, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = admin;
        factory.Disk.TotalBytes = 1000;
        factory.Disk.FreeBytes = 50;

        var summary = await admin.Client.GetFromJsonAsync<StorageSummary>("/api/admin/storage");

        Assert.True(summary!.LowSpace);
    }

    [Fact]
    public async Task The_check_finds_orphans_and_missing_files_and_cleanup_removes_orphans()
    {
        var (factory, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = admin;
        var storage = factory.Services.GetRequiredService<IMediaStorage>();

        // An output directory with no video row (e.g. left by a crash or a manual restore).
        var orphanId = Guid.NewGuid();
        var work = storage.CreateWorkDirectory(orphanId, 1);
        await File.WriteAllBytesAsync(Path.Combine(work, "index.m3u8"), new byte[500]);
        storage.Publish(orphanId, work);

        // A Ready video whose files vanished.
        var broken = await factory.SeedVideoAsync("AppA", status: VideoStatus.Ready);
        var healthy = await factory.SeedVideoAsync("AppA", withFiles: true);

        var check = await (await admin.Client.PostAsync("/api/admin/storage/check", null)).Content.ReadFromJsonAsync<StorageCheckSummary>();

        Assert.Equal(1, check!.OrphanCount);
        Assert.Equal(500, check.OrphanBytes);
        Assert.Equal([broken.PublicId], check.VideosMissingFiles);
        Assert.Equal(VideoStatus.Failed, (await factory.FindVideoAsync(broken.Id))!.Status);
        Assert.Equal(VideoStatus.Ready, (await factory.FindVideoAsync(healthy.Id))!.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.PostAsync("/api/admin/storage/orphans/delete", null)).StatusCode);

        Assert.DoesNotContain(orphanId, storage.ListPublishedVideoIds());
        Assert.NotNull(storage.GetAssetPath(healthy.Id, "index.m3u8"));
        var after = await admin.Client.GetFromJsonAsync<StorageSummary>("/api/admin/storage");
        Assert.Equal(0, after!.LastCheck!.OrphanCount);
    }

    [Fact]
    public async Task Recorded_sizes_are_corrected()
    {
        var (factory, admin) = await StartAsync();
        await using var _ = factory;
        using var __ = admin;
        var video = await factory.SeedVideoAsync("AppA", withFiles: true); // seeded with StorageBytes = 0

        var check = await (await admin.Client.PostAsync("/api/admin/storage/check", null)).Content.ReadFromJsonAsync<StorageCheckSummary>();

        Assert.Equal(1, check!.SizesCorrected);
        var stored = (await factory.FindVideoAsync(video.Id))!;
        Assert.Equal(factory.Services.GetRequiredService<IMediaStorage>().Measure(video.Id).Total, stored.StorageBytes);
    }
}
