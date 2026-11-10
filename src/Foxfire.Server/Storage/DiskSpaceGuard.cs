using Foxfire.Server.Uploads;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Storage;

/// <summary>Decides whether there is room to accept (and later process) an upload of a given size.</summary>
public sealed class DiskSpaceGuard(IStorageStats stats, IOptions<StorageOptions> storage, IOptions<UploadOptions> uploads)
{
    public long RequiredBytes(long uploadBytes) =>
        (long)Math.Ceiling(uploadBytes * uploads.Value.DiskHeadroomFactor) + storage.Value.ReserveBytes;

    public bool HasRoomFor(long uploadBytes) => stats.GetDrive().FreeBytes >= RequiredBytes(uploadBytes);
}
