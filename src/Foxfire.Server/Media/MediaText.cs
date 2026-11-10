using System.Globalization;
using System.Text;

namespace Foxfire.Server.Media;

public static class MediaText
{
    /// <summary>
    /// Rejects inputs whose first bytes are an HLS playlist or an ffconcat list. Such files make ffmpeg
    /// open other files or URLs, which an uploaded "video" must never be able to do.
    /// </summary>
    public static bool LooksLikePlaylist(ReadOnlySpan<byte> head)
    {
        var text = Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("ffconcat", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Progress from an ffmpeg <c>-progress</c> line (<c>out_time_us=…</c>), as 0–100, or null for other lines.</summary>
    public static double? ParseProgress(string line, double durationSeconds)
    {
        const string key = "out_time_us=";
        if (durationSeconds <= 0 || !line.StartsWith(key, StringComparison.Ordinal) ||
            !long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
        {
            return null;
        }

        return Math.Clamp(micros / 1_000_000.0 / durationSeconds * 100, 0, 100);
    }

    /// <summary>Last lines of stderr with server paths stripped, safe to store and show to admins/apps.</summary>
    public static string SanitizeError(IEnumerable<string> stderrTail, IEnumerable<string> pathsToHide, int maxLength = 1500)
    {
        var text = string.Join('\n', stderrTail).Trim();
        foreach (var path in pathsToHide.Where(p => !string.IsNullOrEmpty(p)).OrderByDescending(p => p.Length))
        {
            text = text.Replace(path, "<file>", StringComparison.Ordinal);
        }

        if (text.Length == 0)
        {
            text = "ffmpeg failed without an error message.";
        }

        return text.Length <= maxLength ? text : "…" + text[^(maxLength - 1)..];
    }
}
