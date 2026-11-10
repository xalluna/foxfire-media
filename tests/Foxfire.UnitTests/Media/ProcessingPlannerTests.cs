using System.Text;
using Foxfire.Server.Media;
using Foxfire.Server.Videos;
using static Foxfire.UnitTests.Media.ProbeFixtures;

namespace Foxfire.UnitTests.Media;

public sealed class ProcessingPlannerTests
{
    private static readonly MediaOptions Defaults = new();

    private static ProcessingPlan PlanFor(string json, MediaOptions? options = null) =>
        ProcessingPlanner.Plan(ProbeResult.Parse(json), options ?? Defaults);

    [Fact]
    public void H264_and_aac_in_mp4_is_remuxed()
    {
        var plan = PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "5400.5", Video(0, "h264"), Audio(1, "aac")));

        Assert.Equal(ProcessingMode.Remux, plan.Mode);
        Assert.True(plan.CopyVideo);
        Assert.True(plan.CopyAudio);
        Assert.Equal(0, plan.VideoStreamIndex);
        Assert.Equal(1, plan.AudioStreamIndex);
    }

    [Fact]
    public void H264_with_ac3_in_matroska_copies_video_and_transcodes_audio()
    {
        var plan = PlanFor(Build("matroska,webm", "7200", Video(0, "h264"), Audio(1, "ac3", profile: null, channels: 6), Subtitle(2)));

        Assert.Equal(ProcessingMode.AudioTranscode, plan.Mode);
        Assert.True(plan.CopyVideo);
        Assert.False(plan.CopyAudio);
    }

    [Theory]
    [InlineData("hevc", "Main 10", "yuv420p10le")]
    [InlineData("h264", "High 10", "yuv420p10le")]
    [InlineData("h264", "High 4:2:2", "yuv422p")]
    [InlineData("vp9", "Profile 0", "yuv420p")]
    [InlineData("mpeg4", "Simple Profile", "yuv420p")]
    [InlineData("av1", "Main", "yuv420p")]
    public void Anything_but_8bit_420_h264_is_transcoded(string codec, string profile, string pixFmt)
    {
        var plan = PlanFor(Build("matroska,webm", "60", Video(0, codec, profile, pixFmt), Audio(1, "opus", profile: null)));

        Assert.Equal(ProcessingMode.Transcode, plan.Mode);
        Assert.False(plan.CopyVideo);
        Assert.False(plan.CopyAudio);
    }

    [Fact]
    public void Interlaced_h264_is_deinterlaced()
    {
        var plan = PlanFor(Build("mpegts", "60", Video(0, "h264", fieldOrder: "tt"), Audio(1, "aac")));

        Assert.Equal(ProcessingMode.Transcode, plan.Mode);
        Assert.True(plan.Deinterlace);
        Assert.Contains("yadif", ProcessingPlanner.HlsArguments(plan, "in", "out", Defaults));
    }

    [Fact]
    public void Video_without_audio_is_remuxed_without_an_audio_map()
    {
        var plan = PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "30", Video(0, "h264", "Main")));

        Assert.Equal(ProcessingMode.Remux, plan.Mode);
        Assert.Null(plan.AudioStreamIndex);
        Assert.DoesNotContain("-c:a", ProcessingPlanner.HlsArguments(plan, "in", "out", Defaults));
    }

    [Fact]
    public void Cover_art_is_not_mistaken_for_the_video_and_default_streams_win()
    {
        var plan = PlanFor(Build("matroska,webm", "60",
            Video(0, "mjpeg", profile: null, pixFmt: "yuvj420p", attachedPic: true, isDefault: false),
            Video(1, "h264", isDefault: false),
            Audio(2, "aac", isDefault: false),
            Audio(3, "aac", isDefault: true)));

        Assert.Equal(1, plan.VideoStreamIndex);
        Assert.Equal(3, plan.AudioStreamIndex);
    }

    [Theory]
    [InlineData("hls")]
    [InlineData("concat")]
    [InlineData("image2")]
    [InlineData("lavfi")]
    [InlineData("mov,mp4,hls")]
    [InlineData("")]
    public void Dangerous_or_unknown_containers_are_rejected(string format) =>
        Assert.Throws<MediaRejectedException>(() => PlanFor(Build(format, "60", Video(0, "h264"))));

    [Fact]
    public void Audio_only_files_are_rejected() =>
        Assert.Throws<MediaRejectedException>(() => PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "60", Audio(0, "aac"))));

    [Fact]
    public void Zero_duration_is_rejected() =>
        Assert.Throws<MediaRejectedException>(() => PlanFor(Build("matroska,webm", "0", Video(0, "h264"))));

    [Fact]
    public void Optional_caps_force_a_scaled_transcode()
    {
        var options = new MediaOptions { MaxHeight = 1080, MaxBitrateKbps = 8000 };

        var tall = PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "60", Video(0, "h264", width: 3840, height: 2160), Audio(1, "aac")), options);
        var fast = PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "60", Video(0, "h264", bitRate: "30000000"), Audio(1, "aac")), options);
        var fine = PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "60", Video(0, "h264", bitRate: "5000000"), Audio(1, "aac")), options);

        Assert.Equal(ProcessingMode.Transcode, tall.Mode);
        Assert.Equal(1080, tall.ScaleToHeight);
        Assert.Contains("scale=-2:1080", ProcessingPlanner.HlsArguments(tall, "in", "out", options));
        Assert.Equal(ProcessingMode.Transcode, fast.Mode);
        Assert.Contains("8000k", ProcessingPlanner.HlsArguments(fast, "in", "out", options));
        Assert.Equal(ProcessingMode.Remux, fine.Mode);
    }

    [Fact]
    public void No_caps_by_default_so_4k_h264_is_remuxed()
    {
        var plan = PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "60", Video(0, "h264", width: 3840, height: 2160, bitRate: "40000000"), Audio(1, "aac")));

        Assert.Equal(ProcessingMode.Remux, plan.Mode);
    }

    [Fact]
    public void Hls_arguments_restrict_protocols_and_write_fmp4_hls_into_the_output_directory()
    {
        var plan = PlanFor(Build("matroska,webm", "60", Video(0, "hevc", "Main"), Audio(1, "aac")));
        var output = Path.Combine("work", "abc-1");

        var args = ProcessingPlanner.HlsArguments(plan, "source.mkv", output, Defaults);

        Assert.Equal("file", args[args.ToList().IndexOf("-protocol_whitelist") + 1]);
        Assert.True(args.ToList().IndexOf("-protocol_whitelist") < args.ToList().IndexOf("-i"), "the whitelist must precede the input");
        Assert.Equal("source.mkv", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Contains("libx264", args);
        Assert.Contains("fmp4", args);
        Assert.Contains("vod", args);
        Assert.Contains("init.mp4", args);
        Assert.Contains("expr:gte(t,n_forced*6)", args);
        Assert.Equal(Path.Combine(output, "seg_%05d.m4s"), args[args.ToList().IndexOf("-hls_segment_filename") + 1]);
        Assert.Equal(Path.Combine(output, "index.m3u8"), args[^1]);
        Assert.Contains("-sn", args);
        Assert.Equal("-1", args[args.ToList().IndexOf("-map_metadata") + 1]);
    }

    [Fact]
    public void Remux_arguments_copy_both_streams()
    {
        var plan = PlanFor(Build("mov,mp4,m4a,3gp,3g2,mj2", "60", Video(0, "h264"), Audio(1, "aac")));

        var args = ProcessingPlanner.HlsArguments(plan, "in.mp4", "out", Defaults).ToList();

        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("libx264", args);
    }

    [Fact]
    public void Poster_is_taken_at_ten_percent_and_capped_at_720p()
    {
        var args = ProcessingPlanner.PosterArguments("in.mp4", "poster.jpg", 600, 2160).ToList();

        Assert.Equal("60", args[args.IndexOf("-ss") + 1]);
        Assert.Contains("scale=-2:720", args);
        Assert.True(args.IndexOf("-protocol_whitelist") < args.IndexOf("-i"));
    }
}

public sealed class MediaTextTests
{
    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-VERSION:3")]
    [InlineData("﻿  #EXTM3U")]
    [InlineData("ffconcat version 1.0\nfile '/etc/passwd'")]
    [InlineData("#EXT-X-TARGETDURATION:10")]
    public void Playlists_are_detected(string head) => Assert.True(MediaText.LooksLikePlaylist(Encoding.UTF8.GetBytes(head)));

    [Fact]
    public void Real_media_headers_are_not_playlists()
    {
        byte[] mp4 = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];
        byte[] mkv = [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81];

        Assert.False(MediaText.LooksLikePlaylist(mp4));
        Assert.False(MediaText.LooksLikePlaylist(mkv));
        Assert.False(MediaText.LooksLikePlaylist([]));
    }

    [Theory]
    [InlineData("out_time_us=30000000", 60, 50)]
    [InlineData("out_time_us=90000000", 60, 100)]
    [InlineData("out_time_us=0", 60, 0)]
    public void Progress_is_parsed(string line, double duration, double expected) =>
        Assert.Equal(expected, MediaText.ParseProgress(line, duration));

    [Theory]
    [InlineData("frame=100")]
    [InlineData("out_time_us=N/A")]
    [InlineData("progress=continue")]
    public void Other_progress_lines_are_ignored(string line) => Assert.Null(MediaText.ParseProgress(line, 60));

    [Fact]
    public void Errors_have_paths_stripped_and_are_bounded()
    {
        var source = Path.Combine("data", "media", "sources", "abc", "source.mkv");

        var message = MediaText.SanitizeError([$"{source}: Invalid data found when processing input", "line2"], [source]);
        var bounded = MediaText.SanitizeError([new string('x', 5000)], [], maxLength: 100);

        Assert.DoesNotContain(source, message, StringComparison.Ordinal);
        Assert.Contains("<file>: Invalid data", message, StringComparison.Ordinal);
        Assert.Equal(100, bounded.Length);
        Assert.Equal("ffmpeg failed without an error message.", MediaText.SanitizeError([], []));
    }
}
