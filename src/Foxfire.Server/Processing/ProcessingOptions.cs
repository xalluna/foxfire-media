namespace Foxfire.Server.Processing;

public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    /// <summary>Simultaneous ffmpeg jobs. Transcoding is CPU-bound; 1 is a sensible default.</summary>
    public int Concurrency { get; set; } = 1;

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before a failed attempt is retried (multiplied by the attempt number).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A running job whose lease lapses (worker died) is reclaimed.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the lease is renewed and progress persisted while a job runs.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Fallback poll in case a wake-up signal is missed.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Keep the original upload after a successful encode (allows re-processing). On by default.</summary>
    public bool KeepSource { get; set; } = true;
}
