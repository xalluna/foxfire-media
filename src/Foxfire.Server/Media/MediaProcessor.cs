using Foxfire.Server.Videos;
using Microsoft.Extensions.Options;

namespace Foxfire.Server.Media;

/// <summary>What processing learned about the video.</summary>
public sealed record MediaFacts(ProcessingMode Mode, long DurationMs, int Width, int Height, string VideoCodec, string? AudioCodec);

/// <summary>ffmpeg failed in a way that might succeed on retry (crash, OOM, I/O).</summary>
public sealed class MediaProcessingException(string message) : Exception(message);

/// <summary>
/// Turns a kept source file into a single-rendition HLS VOD (index.m3u8, init.mp4, seg_NNNNN.m4s)
/// plus poster.jpg inside <paramref name="outputDirectory"/>.
/// </summary>
public interface IMediaProcessor
{
    /// <exception cref="MediaRejectedException">The input can never be processed.</exception>
    /// <exception cref="MediaProcessingException">A transient failure; the job may be retried.</exception>
    Task<MediaFacts> ProcessAsync(string sourcePath, string outputDirectory, IProgress<double> progress, CancellationToken ct);
}

public sealed class FfmpegMediaProcessor(IOptions<MediaOptions> options) : IMediaProcessor
{
    private const int SniffBytes = 64;

    public async Task<MediaFacts> ProcessAsync(string sourcePath, string outputDirectory, IProgress<double> progress, CancellationToken ct)
    {
        var settings = options.Value;
        var hidden = new[] { sourcePath, outputDirectory, Path.GetDirectoryName(sourcePath) ?? "" };

        await RejectPlaylistsAsync(sourcePath, ct);

        var probe = await ProbeAsync(sourcePath, settings, hidden, ct);
        var plan = ProcessingPlanner.Plan(probe, settings);

        var hls = await ProcessRunner.RunAsync(
            settings.FfmpegPath,
            ProcessingPlanner.HlsArguments(plan, sourcePath, outputDirectory, settings),
            line =>
            {
                if (MediaText.ParseProgress(line, probe.DurationSeconds) is { } percent)
                {
                    progress.Report(percent);
                }
            },
            ct);
        if (hls.ExitCode != 0 || !File.Exists(Path.Combine(outputDirectory, "index.m3u8")))
        {
            throw new MediaProcessingException(MediaText.SanitizeError(hls.StandardErrorTail, hidden));
        }

        var poster = await ProcessRunner.RunAsync(
            settings.FfmpegPath,
            ProcessingPlanner.PosterArguments(sourcePath, Path.Combine(outputDirectory, "poster.jpg"), probe.DurationSeconds, probe.Video!.Height),
            null,
            ct);
        if (poster.ExitCode != 0)
        {
            throw new MediaProcessingException("Poster: " + MediaText.SanitizeError(poster.StandardErrorTail, hidden));
        }

        progress.Report(100);
        var video = probe.Video!;
        return new MediaFacts(
            plan.Mode,
            (long)Math.Round(probe.DurationSeconds * 1000),
            plan.ScaleToHeight is { } h ? (int)Math.Round(video.Width * (h / (double)video.Height) / 2) * 2 : video.Width,
            plan.ScaleToHeight ?? video.Height,
            "h264",
            probe.Audio is null ? null : "aac");
    }

    private static async Task RejectPlaylistsAsync(string sourcePath, CancellationToken ct)
    {
        var head = new byte[SniffBytes];
        await using var stream = File.OpenRead(sourcePath);
        var read = await stream.ReadAtLeastAsync(head, SniffBytes, throwOnEndOfStream: false, ct);
        if (MediaText.LooksLikePlaylist(head.AsSpan(0, read)))
        {
            throw new MediaRejectedException("Playlists and concat lists are not accepted as uploads.");
        }
    }

    private static async Task<ProbeResult> ProbeAsync(string sourcePath, MediaOptions settings, string[] hidden, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            settings.FfprobePath,
            ["-v", "error", "-protocol_whitelist", "file", "-print_format", "json", "-show_format", "-show_streams", sourcePath],
            null,
            ct,
            captureStdout: true);

        if (result.ExitCode != 0)
        {
            throw new MediaRejectedException("The file could not be read as a video: " + MediaText.SanitizeError(result.StandardErrorTail, hidden, 500));
        }

        try
        {
            return ProbeResult.Parse(result.StandardOutput);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new MediaRejectedException("The file could not be read as a video.");
        }
    }
}
