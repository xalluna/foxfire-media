using Foxfire.Server.Storage;

namespace Foxfire.UnitTests.Storage;

public sealed class DriveSelectionTests
{
    [Fact]
    public void Picks_the_most_specific_mount_point()
    {
        var root = OperatingSystem.IsWindows() ? @"D:\media\foxfire" : "/data/media";
        var mounts = OperatingSystem.IsWindows()
            ? new[] { @"C:\", @"D:\", @"D:\media\" }
            : new[] { "/", "/data", "/data/media", "/data/media2" };

        var selected = DriveStorageStats.SelectMount(root, mounts);

        Assert.Equal(OperatingSystem.IsWindows() ? @"D:\media\" : "/data/media", selected);
    }

    [Fact]
    public void Sibling_prefixes_are_not_confused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\", DriveStorageStats.SelectMount(@"C:\data\media2\x", [@"C:\", @"C:\data\media"]));
        }
        else
        {
            Assert.Equal("/", DriveStorageStats.SelectMount("/data/media2/x", ["/", "/data/media"]));
        }
    }

    [Fact]
    public void Real_drive_report_is_sane()
    {
        var root = Path.Combine(Path.GetTempPath(), "foxfire-unit", Guid.NewGuid().ToString("N"));
        try
        {
            var report = new DriveStorageStats(new LocalMediaStorage(root)).GetDrive();

            Assert.True(report.TotalBytes > 0);
            Assert.InRange(report.FreeBytes, 0, report.TotalBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
