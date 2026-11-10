using Foxfire.Server.Uploads;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Storage;

/// <summary>Decides whether there is room to accept (and later process) an upload of a given size.</summary>
public sealed class DiskSpaceGuard(IStorageStats stats, MediaVolume volume, IOptions<StorageOptions> storage, IOptions<UploadOptions> uploads)
{
    public long RequiredBytes(long uploadBytes) =>
        (long)Math.Ceiling(uploadBytes * uploads.Value.DiskHeadroomFactor) + storage.Value.ReserveBytes;

    /// <summary>False when the drive lacks space, or when the media drive itself is not mounted.</summary>
    public bool HasRoomFor(long uploadBytes) =>
        volume.IsAvailable && stats.GetDrive().FreeBytes >= RequiredBytes(uploadBytes);
}
