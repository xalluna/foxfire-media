namespace Foxfire.Server.Storage;

public sealed record DriveReport(string MountPoint, string Format, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}

public interface IStorageStats
{
    /// <summary>Capacity of the device that holds the media root.</summary>
    DriveReport GetDrive();
}

public sealed class DriveStorageStats(IMediaStorage storage) : IStorageStats
{
    public DriveReport GetDrive()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => (d.RootDirectory.FullName, d))
            .ToList();

        var mount = SelectMount(storage.Root, drives.Select(d => d.FullName));
        var drive = mount is null ? new DriveInfo(storage.Root) : drives.First(d => d.FullName == mount).d;

        return new DriveReport(drive.RootDirectory.FullName, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace);
    }

    /// <summary>The mount point that is the longest prefix of <paramref name="path"/> (bind mounts nest inside /).</summary>
    public static string? SelectMount(string path, IEnumerable<string> mountPoints)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return mountPoints
            .Where(m => full.StartsWith(m.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(m => m.TrimEnd(Path.DirectorySeparatorChar).Length)
            .FirstOrDefault();
    }
}
