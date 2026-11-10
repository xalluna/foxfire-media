using Foxfire.Server.Storage;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>Settable drive capacity so disk-space rules can be exercised.</summary>
public sealed class FakeStorageStats : IStorageStats
{
    public long TotalBytes { get; set; } = 10L * 1024 * 1024 * 1024 * 1024;

    public long FreeBytes { get; set; } = 5L * 1024 * 1024 * 1024 * 1024;

    public DriveReport GetDrive() => new("/fake", "testfs", TotalBytes, FreeBytes);
}
