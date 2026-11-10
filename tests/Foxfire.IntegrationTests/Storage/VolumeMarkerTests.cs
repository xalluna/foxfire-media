using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Security;
using Foxfire.Server.Storage;

namespace Foxfire.IntegrationTests.Storage;

public sealed class VolumeMarkerTests
{
    [Fact]
    public async Task Without_the_marker_the_server_refuses_to_start_and_writes_nothing()
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        factory.Settings["Storage:RequireVolumeMarker"] = "true";
        Directory.CreateDirectory(factory.StorageRoot); // an empty mount point: the drive is not mounted

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        while (ex is not FoxfireConfigurationException && ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        Assert.Contains(MediaVolume.MarkerFileName, Assert.IsType<FoxfireConfigurationException>(ex).Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(factory.StorageRoot)); // no uploads/, sources/, videos/ ...
    }

    [Fact]
    public async Task With_the_marker_it_starts_and_losing_the_drive_turns_readiness_and_uploads_off()
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        var key = factory.AddApp("AppA");
        factory.Settings["Storage:RequireVolumeMarker"] = "true";
        Directory.CreateDirectory(factory.StorageRoot);
        var marker = Path.Combine(factory.StorageRoot, MediaVolume.MarkerFileName);
        await File.WriteAllTextAsync(marker, "");
        using var app = factory.CreateAppClient(key);
        var mint = new { title = "x", filename = "x.mp4", sizeBytes = 10 };

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/readyz")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.PostAsJsonAsync("/api/v1/uploads", mint)).StatusCode);

        File.Delete(marker); // the drive goes away while running

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await factory.CreateClient().GetAsync("/readyz")).StatusCode);
        Assert.Equal(HttpStatusCode.InsufficientStorage, (await app.PostAsJsonAsync("/api/v1/uploads", mint)).StatusCode);
    }
}
