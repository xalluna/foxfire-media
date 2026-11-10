using System.Diagnostics;
using Foxfire.Server.Media;
using Foxfire.Server.Videos;
using Microsoft.Extensions.Options;

namespace Foxfire.MediaTests;

/// <summary>
/// Runs the real ffprobe/ffmpeg pipeline on synthetic inputs generated with lavfi.
/// Skipped when ffmpeg is not on PATH (CI installs it).
/// </summary>
[Trait("Category", "RequiresFfmpeg")]
public sealed class FfmpegProcessorTests : IDisposable
{
    private static readonly Lazy<bool> FfmpegAvailable = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foxfire-media-tests", Guid.NewGuid().ToString("N"));
    private readonly FfmpegMediaProcessor _processor = new(Options.Create(new MediaOptions { X264Preset = "ultrafast" }));

    public FfmpegProcessorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static async Task Ffmpeg(params string[] args)
    {
        var result = await ProcessRunner.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", .. args], null, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, string.Join('\n', result.StandardErrorTail));
    }

    private async Task<string> Generate(string name, params string[] encode)
    {
        var path = Path.Combine(_dir, name);
        await Ffmpeg(["-f", "lavfi", "-i", "testsrc2=duration=14:size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:duration=14", .. encode, "-shortest", path]);
        return path;
    }

    private async Task<(MediaFacts Facts, string Output, string Playlist)> RunPipeline(string source)
    {
        var output = Path.Combine(_dir, "out-" + Path.GetFileNameWithoutExtension(source));
        Directory.CreateDirectory(output);
        var reports = new List<double>();
        var facts = await _processor.ProcessAsync(source, output, new Progress<double>(reports.Add), TestContext.Current.CancellationToken);
        return (facts, output, await File.ReadAllTextAsync(Path.Combine(output, "index.m3u8"), TestContext.Current.CancellationToken));
    }

    private static async Task<string> ProbeCodecs(string playlist)
    {
        var result = await ProcessRunner.RunAsync(
            "ffprobe",
            ["-v", "error", "-protocol_whitelist", "file", "-show_entries", "stream=codec_name", "-of", "csv=p=0", playlist],
            null,
            TestContext.Current.CancellationToken,
            captureStdout: true);
        return result.StandardOutput.Replace("\r", "", StringComparison.Ordinal).Trim();
    }

    private static void AssertValidHls(string output, string playlist)
    {
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4\"", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("/", playlist.Split('\n').Where(l => !l.StartsWith('#')).Aggregate("", string.Concat), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "init.mp4")));
        Assert.True(File.Exists(Path.Combine(output, "poster.jpg")));
        var segments = Directory.GetFiles(output, "seg_*.m4s");
        Assert.True(segments.Length >= 2, $"expected at least 2 segments, got {segments.Length}");
        Assert.All(Directory.GetFiles(output).Select(Path.GetFileName), name =>
            Assert.Matches(@"^(index\.m3u8|init\.mp4|seg_\d{5}\.m4s|poster\.jpg)$", name!));
    }

    [Fact]
    public async Task H264_aac_mp4_is_remuxed_to_valid_hls()
    {
        Assert.SkipUnless(FfmpegAvailable.Value, "ffmpeg is not installed");
        var source = await Generate("h264.mp4", "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "aac");

        var (facts, output, playlist) = await RunPipeline(source);

        Assert.Equal(ProcessingMode.Remux, facts.Mode);
        Assert.InRange(facts.DurationMs, 13_000, 15_000);
        Assert.Equal(640, facts.Width);
        Assert.Equal(360, facts.Height);
        AssertValidHls(output, playlist);
        Assert.Equal("h264\naac", await ProbeCodecs(Path.Combine(output, "index.m3u8")));
    }

    [Fact]
    public async Task Hevc_opus_mkv_is_transcoded_to_h264_aac()
    {
        Assert.SkipUnless(FfmpegAvailable.Value, "ffmpeg is not installed");
        var source = await Generate("hevc.mkv", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-c:a", "libopus");

        var (facts, output, playlist) = await RunPipeline(source);

        Assert.Equal(ProcessingMode.Transcode, facts.Mode);
        AssertValidHls(output, playlist);
        Assert.Equal("h264\naac", await ProbeCodecs(Path.Combine(output, "index.m3u8")));
    }

    [Fact]
    public async Task H264_with_mp3_in_avi_copies_video_and_transcodes_audio()
    {
        Assert.SkipUnless(FfmpegAvailable.Value, "ffmpeg is not installed");
        var source = await Generate("mixed.mkv", "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "libmp3lame");

        var (facts, output, playlist) = await RunPipeline(source);

        Assert.Equal(ProcessingMode.AudioTranscode, facts.Mode);
        AssertValidHls(output, playlist);
        Assert.Equal("h264\naac", await ProbeCodecs(Path.Combine(output, "index.m3u8")));
    }

    [Fact]
    public async Task Mpeg4_avi_is_transcoded()
    {
        Assert.SkipUnless(FfmpegAvailable.Value, "ffmpeg is not installed");
        var source = await Generate("legacy.avi", "-c:v", "mpeg4", "-c:a", "libmp3lame");

        var (facts, output, playlist) = await RunPipeline(source);

        Assert.Equal(ProcessingMode.Transcode, facts.Mode);
        AssertValidHls(output, playlist);
    }

    [Fact]
    public async Task A_playlist_upload_is_rejected_before_ffmpeg_touches_it()
    {
        Assert.SkipUnless(FfmpegAvailable.Value, "ffmpeg is not installed");
        var source = Path.Combine(_dir, "evil.mp4");
        await File.WriteAllTextAsync(source, "#EXTM3U\n#EXTINF:1,\nfile:///etc/passwd\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<MediaRejectedException>(() => RunPipeline(source));
    }

    [Fact]
    public async Task Garbage_is_rejected()
    {
        Assert.SkipUnless(FfmpegAvailable.Value, "ffmpeg is not installed");
        var source = Path.Combine(_dir, "garbage.mp4");
        await File.WriteAllBytesAsync(source, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<MediaRejectedException>(() => RunPipeline(source));
    }
}
