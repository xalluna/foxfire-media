namespace Foxfire.Server.Media;

public sealed class MediaOptions
{
    public const string Section = "Media";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>libx264 preset used when a source must be transcoded.</summary>
    public string X264Preset { get; set; } = "veryfast";

    /// <summary>libx264 constant rate factor (lower is higher quality).</summary>
    public int Crf { get; set; } = 20;

    public string AudioBitrate { get; set; } = "160k";

    /// <summary>Target HLS segment length in seconds.</summary>
    public int SegmentSeconds { get; set; } = 6;

    /// <summary>Optional cap: sources taller than this are transcoded down. Off by default.</summary>
    public int? MaxHeight { get; set; }

    /// <summary>Optional cap: sources above this video bitrate are transcoded (and capped). Off by default.</summary>
    public int? MaxBitrateKbps { get; set; }

    /// <summary>ffmpeg -threads (0 = let ffmpeg decide).</summary>
    public int Threads { get; set; }
}
