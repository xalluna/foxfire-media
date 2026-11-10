using System.Text.RegularExpressions;

namespace Foxfire.Server.Storage;

/// <summary>
/// Local-disk layout (all under one filesystem so publishing is a rename):
/// <code>
/// uploads/                       resumable uploads in progress
/// sources/{id}/source.{ext}      kept originals, never served
/// work/{id}-{attempt}/           ffmpeg output while processing
/// videos/{id}/                   published HLS output + poster.jpg
/// </code>
/// </summary>
public sealed partial class LocalMediaStorage : IMediaStorage
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _sources;
    private readonly string _work;
    private readonly string _videos;

    public LocalMediaStorage(string root)
    {
        Root = Path.GetFullPath(root);
        UploadsDirectory = Path.Combine(Root, "uploads");
        _sources = Path.Combine(Root, "sources");
        _work = Path.Combine(Root, "work");
        _videos = Path.Combine(Root, "videos");

        foreach (var dir in new[] { Root, UploadsDirectory, _sources, _work, _videos })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string Root { get; }

    public string UploadsDirectory { get; }

    [GeneratedRegex(@"^(index\.m3u8|init\.mp4|seg_\d{5}\.m4s|poster\.jpg)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetName();

    [GeneratedRegex(@"^\.[a-z0-9]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex Extension();

    public static bool IsAllowedAssetName(string name) => AssetName().IsMatch(name);

    public string AdoptSource(Guid videoId, string uploadedFilePath, string extension)
    {
        extension = extension.ToLowerInvariant();
        if (!Extension().IsMatch(extension))
        {
            extension = ".bin";
        }

        var dir = Path.Combine(_sources, Id(videoId));
        DeleteDirectory(dir);
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, "source" + extension);
        File.Move(uploadedFilePath, destination);
        return destination;
    }

    public string? FindSource(Guid videoId)
    {
        var dir = Path.Combine(_sources, Id(videoId));
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "source.*").FirstOrDefault() : null;
    }

    public string CreateWorkDirectory(Guid videoId, int attempt)
    {
        var dir = Path.Combine(_work, $"{Id(videoId)}-{attempt}");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Publish(Guid videoId, string workDirectory)
    {
        var full = Path.GetFullPath(workDirectory);
        if (!IsUnder(full, _work))
        {
            throw new InvalidOperationException("Only work directories can be published.");
        }

        var target = Path.Combine(_videos, Id(videoId));
        string? previous = null;
        if (Directory.Exists(target))
        {
            previous = Path.Combine(_work, $"{Id(videoId)}-retired-{Guid.NewGuid():N}");
            Directory.Move(target, previous);
        }

        Directory.Move(full, target);

        if (previous is not null)
        {
            Directory.Delete(previous, recursive: true);
        }
    }

    public string? GetAssetPath(Guid videoId, string assetName)
    {
        if (!IsAllowedAssetName(assetName))
        {
            return null;
        }

        var dir = Path.Combine(_videos, Id(videoId));
        var path = Path.GetFullPath(Path.Combine(dir, assetName));
        return IsUnder(path, dir) && File.Exists(path) ? path : null;
    }

    public VideoFootprint Measure(Guid videoId) =>
        new(SizeOf(Path.Combine(_videos, Id(videoId))), SizeOf(Path.Combine(_sources, Id(videoId))));

    public void Delete(Guid videoId)
    {
        DeleteDirectory(Path.Combine(_videos, Id(videoId)));
        DeleteDirectory(Path.Combine(_sources, Id(videoId)));
        foreach (var dir in Directory.EnumerateDirectories(_work, Id(videoId) + "-*").ToList())
        {
            DeleteDirectory(dir);
        }
    }

    public void CleanWorkDirectories(IReadOnlySet<Guid> keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(_work).ToList())
        {
            var name = Path.GetFileName(dir);
            if (name.Length >= 32 && Guid.TryParseExact(name[..32], "N", out var id) && keep.Contains(id))
            {
                continue;
            }

            DeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Idempotent and tolerant of a concurrent remover (e.g. a worker cleaning its scratch directory while
    /// the video is deleted): "not found" is success, and transient sharing errors are retried briefly.
    /// </summary>
    private static void DeleteDirectory(string dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                if (!Directory.Exists(dir))
                {
                    return;
                }

                Thread.Sleep(50 * attempt);
            }
        }
    }

    public IEnumerable<Guid> ListPublishedVideoIds() => ListIds(_videos);

    public IEnumerable<Guid> ListSourceVideoIds() => ListIds(_sources);

    public long MeasureUploads() => SizeOf(UploadsDirectory);

    public long MeasureWork() => SizeOf(_work);

    private static IEnumerable<Guid> ListIds(string parent)
    {
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (Guid.TryParseExact(Path.GetFileName(dir), "N", out var id))
            {
                yield return id;
            }
        }
    }

    private static string Id(Guid videoId) => videoId.ToString("N");

    private static long SizeOf(string dir) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    private static bool IsUnder(string path, string directory)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }
}
