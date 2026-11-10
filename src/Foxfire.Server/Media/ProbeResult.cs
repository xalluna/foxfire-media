using System.Globalization;
using System.Text.Json;

namespace Foxfire.Server.Media;

public sealed record VideoStreamInfo(int Index, string Codec, string? Profile, string? PixelFormat, int Width, int Height, long? BitRate, string? FieldOrder);

public sealed record AudioStreamInfo(int Index, string Codec, string? Profile, int Channels);

public sealed record ProbeResult(string FormatName, double DurationSeconds, long? BitRate, VideoStreamInfo? Video, AudioStreamInfo? Audio)
{
    /// <summary>Parses <c>ffprobe -print_format json -show_format -show_streams</c> output.</summary>
    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;

        var formatName = format.ValueKind == JsonValueKind.Object ? Str(format, "format_name") ?? "" : "";
        var duration = format.ValueKind == JsonValueKind.Object ? Num(format, "duration") ?? 0 : 0;
        var bitRate = format.ValueKind == JsonValueKind.Object ? Long(format, "bit_rate") : null;

        VideoStreamInfo? video = null;
        AudioStreamInfo? audio = null;
        VideoStreamInfo? firstVideo = null;
        AudioStreamInfo? firstAudio = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                var index = s.TryGetProperty("index", out var i) && i.TryGetInt32(out var idx) ? idx : -1;
                var isDefault = Disposition(s, "default");

                if (type == "video" && !Disposition(s, "attached_pic"))
                {
                    var info = new VideoStreamInfo(
                        index,
                        Str(s, "codec_name") ?? "unknown",
                        Str(s, "profile"),
                        Str(s, "pix_fmt"),
                        (int)(Long(s, "width") ?? 0),
                        (int)(Long(s, "height") ?? 0),
                        Long(s, "bit_rate"),
                        Str(s, "field_order"));
                    firstVideo ??= info;
                    if (isDefault && video is null)
                    {
                        video = info;
                    }

                    duration = duration > 0 ? duration : Num(s, "duration") ?? 0;
                }
                else if (type == "audio")
                {
                    var info = new AudioStreamInfo(index, Str(s, "codec_name") ?? "unknown", Str(s, "profile"), (int)(Long(s, "channels") ?? 0));
                    firstAudio ??= info;
                    if (isDefault && audio is null)
                    {
                        audio = info;
                    }
                }
            }
        }

        return new ProbeResult(formatName, duration, bitRate, video ?? firstVideo, audio ?? firstAudio);
    }

    private static bool Disposition(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        Str(e, name) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }
}
