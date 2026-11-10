namespace Foxfire.UnitTests.Media;

/// <summary>Trimmed real-world shapes of <c>ffprobe -print_format json -show_format -show_streams</c>.</summary>
internal static class ProbeFixtures
{
    public static string Build(string formatName, string duration, params string[] streams) => $$"""
        {
          "streams": [{{string.Join(",", streams)}}],
          "format": { "format_name": "{{formatName}}", "duration": "{{duration}}", "bit_rate": "4500000" }
        }
        """;

    public static string Video(int index, string codec, string? profile = "High", string pixFmt = "yuv420p", int width = 1920, int height = 1080,
        string? fieldOrder = "progressive", bool isDefault = true, bool attachedPic = false, string? bitRate = null) => $$"""
        {
          "index": {{index}}, "codec_name": "{{codec}}", "codec_type": "video",
          {{(profile is null ? "" : $"\"profile\": \"{profile}\",")}}
          "width": {{width}}, "height": {{height}}, "pix_fmt": "{{pixFmt}}",
          {{(fieldOrder is null ? "" : $"\"field_order\": \"{fieldOrder}\",")}}
          {{(bitRate is null ? "" : $"\"bit_rate\": \"{bitRate}\",")}}
          "disposition": { "default": {{(isDefault ? 1 : 0)}}, "attached_pic": {{(attachedPic ? 1 : 0)}} }
        }
        """;

    public static string Audio(int index, string codec, string? profile = "LC", int channels = 2, bool isDefault = true) => $$"""
        {
          "index": {{index}}, "codec_name": "{{codec}}", "codec_type": "audio",
          {{(profile is null ? "" : $"\"profile\": \"{profile}\",")}}
          "channels": {{channels}},
          "disposition": { "default": {{(isDefault ? 1 : 0)}}, "attached_pic": 0 }
        }
        """;

    public static string Subtitle(int index) => $$"""{ "index": {{index}}, "codec_name": "subrip", "codec_type": "subtitle" }""";
}
