namespace Foxfire.Server.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Media root. Relative paths resolve against the content root. Docker sets /data/media.</summary>
    public string Root { get; set; } = "data/media";

    /// <summary>Space kept free beyond what an upload needs before new uploads are refused.</summary>
    public long ReserveBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>The Storage page warns when free space drops below this percentage of the drive.</summary>
    public double LowSpaceWarningPercent { get; set; } = 10;

    /// <summary>
    /// Require <see cref="MediaVolume.MarkerFileName"/> in the root, so media is never written to the OS
    /// disk when a dedicated drive is not mounted. Off by default; turn it on for a dedicated drive.
    /// </summary>
    public bool RequireVolumeMarker { get; set; }

    /// <summary>How often disk and database are compared (orphans, missing files, sizes).</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(1);
}
