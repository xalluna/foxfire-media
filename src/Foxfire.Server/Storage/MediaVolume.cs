using Foxfire.Server.Security;

namespace Foxfire.Server.Storage;

/// <summary>
/// Guards against writing media to the wrong disk. With <c>Storage__RequireVolumeMarker=true</c>, the
/// media root must contain <see cref="MarkerFileName"/> (created once on the dedicated drive). If the drive
/// is not mounted, the mount point is an empty directory on the OS disk: the marker is missing, so the
/// server refuses to start, readiness turns unhealthy, and uploads are refused instead of silently filling
/// the wrong disk.
/// </summary>
public sealed class MediaVolume(string root, bool markerRequired)
{
    public const string MarkerFileName = ".foxfire-media-volume";

    public string Root { get; } = Path.GetFullPath(root);

    public bool MarkerRequired { get; } = markerRequired;

    public string MarkerPath => Path.Combine(Root, MarkerFileName);

    /// <summary>True when media may be written: the marker is not required, or it is present.</summary>
    public bool IsAvailable => !MarkerRequired || File.Exists(MarkerPath);

    /// <summary>Called before anything is created under the root.</summary>
    public void EnsureAvailableAtStartup()
    {
        if (!IsAvailable)
        {
            throw new FoxfireConfigurationException(
            [
                $"Storage__RequireVolumeMarker is set, but '{MarkerPath}' does not exist. Is the media drive mounted? " +
                $"If this is the right drive, create the marker once on it (e.g. touch <drive>/{MarkerFileName}) and restart.",
            ]);
        }
    }
}
