namespace Foxfire.Server.Uploads;

public sealed class UploadOptions
{
    public const string Section = "Uploads";

    /// <summary>How long a minted upload token can be used to start an upload.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>An upload with no progress for this long is failed (token stays consumed).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Hard cap on how long one upload may take from start to finish.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Largest single upload accepted.</summary>
    public long MaxBytes { get; set; } = 200L * 1024 * 1024 * 1024;

    /// <summary>Free space required = size × this factor + Storage:ReserveBytes (source + HLS output + slack).</summary>
    public double DiskHeadroomFactor { get; set; } = 2.2;

    /// <summary>Failed/expired uploads that are not re-minted are deleted after this long.</summary>
    public TimeSpan AbandonedRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);
}
