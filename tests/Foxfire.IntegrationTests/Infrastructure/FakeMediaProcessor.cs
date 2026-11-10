using System.Collections.Concurrent;
using Foxfire.Server.Media;
using Foxfire.Server.Videos;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for ffmpeg: writes a small canned HLS output (the bytes need not decode). Tests can queue
/// failures to exercise retries.
/// </summary>
public sealed class FakeMediaProcessor : IMediaProcessor
{
    public const string Playlist =
        "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" +
        "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.000000,\nseg_00000.m4s\n#EXTINF:6.000000,\nseg_00001.m4s\n#EXT-X-ENDLIST\n";

    private readonly ConcurrentQueue<Exception> _failures = new();

    public int Calls;

    /// <summary>Optional gate a test can hold to keep a job "Processing".</summary>
    public TaskCompletionSource? Gate { get; set; }

    public void FailNext(Exception exception) => _failures.Enqueue(exception);

    public async Task<MediaFacts> ProcessAsync(string sourcePath, string outputDirectory, IProgress<double> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (!File.Exists(sourcePath))
        {
            throw new MediaProcessingException("source missing");
        }

        progress.Report(50);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (_failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "index.m3u8"), Playlist, ct);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "init.mp4"), new byte[64], ct);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "seg_00000.m4s"), new byte[512], ct);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "seg_00001.m4s"), new byte[512], ct);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "poster.jpg"), [0xFF, 0xD8, 0xFF, 0xD9], ct);
        progress.Report(100);

        return new MediaFacts(ProcessingMode.Remux, 12_000, 1280, 720, "h264", "aac");
    }
}
