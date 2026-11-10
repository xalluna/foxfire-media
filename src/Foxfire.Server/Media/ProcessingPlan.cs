using System.Globalization;
using Foxfire.Server.Videos;

namespace Foxfire.Server.Media;

/// <summary>The input was examined and will never be processable (no retry).</summary>
public sealed class MediaRejectedException(string message) : Exception(message);

public sealed record ProcessingPlan(
    ProcessingMode Mode,
    int VideoStreamIndex,
    bool CopyVideo,
    int? AudioStreamIndex,
    bool CopyAudio,
    int? ScaleToHeight,
    bool Deinterlace,
    int? MaxBitrateKbps);

public static class ProcessingPlanner
{
    /// <summary>Containers ffprobe may report for an accepted upload. Playlists, concat lists, images, devices etc. are refused.</summary>
    private static readonly HashSet<string> AllowedFormats = new(StringComparer.Ordinal)
    {
        "mov", "mp4", "m4a", "3gp", "3g2", "mj2", "matroska", "webm", "avi", "mpegts", "flv", "mpeg", "asf",
    };

    private static readonly HashSet<string> CopyableH264Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Constrained Baseline", "Baseline", "Main", "High",
    };

    public static ProcessingPlan Plan(ProbeResult probe, MediaOptions options)
    {
        var formats = probe.FormatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (formats.Length == 0 || !formats.All(AllowedFormats.Contains))
        {
            throw new MediaRejectedException($"Unsupported container format '{probe.FormatName}'.");
        }

        var video = probe.Video ?? throw new MediaRejectedException("The file has no video stream.");
        if (video.Width <= 0 || video.Height <= 0)
        {
            throw new MediaRejectedException("The video stream has no usable dimensions.");
        }

        if (probe.DurationSeconds <= 0)
        {
            throw new MediaRejectedException("The file has no measurable duration.");
        }

        var interlaced = video.FieldOrder is "tt" or "bb" or "tb" or "bt";
        var tooTall = options.MaxHeight is { } maxHeight && video.Height > maxHeight;
        var videoBitrateKbps = (video.BitRate ?? probe.BitRate) / 1000;
        var tooFast = options.MaxBitrateKbps is { } maxRate && videoBitrateKbps > maxRate;

        var copyVideo = video.Codec == "h264" &&
                        video.PixelFormat is "yuv420p" or "yuvj420p" &&
                        video.Profile is not null && CopyableH264Profiles.Contains(video.Profile) &&
                        !interlaced && !tooTall && !tooFast;

        var audio = probe.Audio;
        var copyAudio = audio is { Codec: "aac" } && audio.Profile is null or "LC";

        var mode = !copyVideo ? ProcessingMode.Transcode
            : audio is not null && !copyAudio ? ProcessingMode.AudioTranscode
            : ProcessingMode.Remux;

        return new ProcessingPlan(
            mode,
            video.Index,
            copyVideo,
            audio?.Index,
            copyAudio,
            tooTall ? options.MaxHeight : null,
            interlaced,
            copyVideo ? null : options.MaxBitrateKbps);
    }

    /// <summary>ffmpeg arguments that turn the source into a single-rendition fMP4 HLS VOD in <paramref name="outputDirectory"/>.</summary>
    public static IReadOnlyList<string> HlsArguments(ProcessingPlan plan, string input, string outputDirectory, MediaOptions options)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-protocol_whitelist", "file",
        };
        if (options.Threads > 0)
        {
            args.AddRange(["-threads", Inv(options.Threads)]);
        }

        args.AddRange(["-i", input]);
        args.AddRange(["-map", $"0:{Inv(plan.VideoStreamIndex)}"]);
        if (plan.AudioStreamIndex is { } audioIndex)
        {
            args.AddRange(["-map", $"0:{Inv(audioIndex)}"]);
        }

        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        if (plan.CopyVideo)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            var filters = new List<string>();
            if (plan.Deinterlace)
            {
                filters.Add("yadif");
            }

            if (plan.ScaleToHeight is { } height)
            {
                filters.Add($"scale=-2:{Inv(height - (height % 2))}");
            }

            if (filters.Count > 0)
            {
                args.AddRange(["-vf", string.Join(',', filters)]);
            }

            args.AddRange([
                "-c:v", "libx264", "-preset", options.X264Preset, "-crf", Inv(options.Crf),
                "-pix_fmt", "yuv420p", "-profile:v", "high",
                "-force_key_frames", $"expr:gte(t,n_forced*{Inv(options.SegmentSeconds)})", "-sc_threshold", "0",
            ]);
            if (plan.MaxBitrateKbps is { } maxRate)
            {
                args.AddRange(["-maxrate", $"{Inv(maxRate)}k", "-bufsize", $"{Inv(maxRate * 2)}k"]);
            }
        }

        if (plan.AudioStreamIndex is not null)
        {
            args.AddRange(plan.CopyAudio
                ? ["-c:a", "copy"]
                : ["-c:a", "aac", "-b:a", options.AudioBitrate, "-ac", "2"]);
        }

        args.AddRange([
            "-f", "hls",
            "-hls_time", Inv(options.SegmentSeconds),
            "-hls_playlist_type", "vod",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "init.mp4",
            "-hls_segment_filename", Path.Combine(outputDirectory, "seg_%05d.m4s"),
            "-progress", "pipe:1", "-nostats",
            Path.Combine(outputDirectory, "index.m3u8"),
        ]);
        return args;
    }

    /// <summary>ffmpeg arguments for a JPEG poster frame at 10% of the duration, at most 720p tall.</summary>
    public static IReadOnlyList<string> PosterArguments(string input, string outputFile, double durationSeconds, int sourceHeight)
    {
        var at = Math.Max(0, durationSeconds * 0.1);
        var height = Math.Min(720, sourceHeight);
        height -= height % 2;
        return
        [
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-protocol_whitelist", "file",
            "-ss", at.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", input,
            "-frames:v", "1",
            "-vf", $"scale=-2:{Inv(Math.Max(2, height))}",
            "-q:v", "3",
            outputFile,
        ];
    }

    private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
}
