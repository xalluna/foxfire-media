namespace Foxfire.Server.Storage;

/// <summary>
/// Owns the on-disk layout. Only <see cref="GetAssetPath"/> ever hands out a path that may be
/// served, and only for allow-listed HLS/poster file names inside a published video directory.
/// </summary>
public interface IMediaStorage
{
    string Root { get; }

    /// <summary>Where in-progress resumable uploads are written.</summary>
    string UploadsDirectory { get; }

    /// <summary>Moves a completed upload into sources/{videoId}/source{extension}. Returns the new path.</summary>
    string AdoptSource(Guid videoId, string uploadedFilePath, string extension);

    /// <summary>The kept original for a video, or null when it is gone.</summary>
    string? FindSource(Guid videoId);

    /// <summary>Creates (or empties) the scratch directory a processing attempt writes into.</summary>
    string CreateWorkDirectory(Guid videoId, int attempt);

    /// <summary>Atomically swaps a finished work directory in as the video's published output.</summary>
    void Publish(Guid videoId, string workDirectory);

    /// <summary>Validated physical path of a published asset, or null if the name is not allowed or the file is absent.</summary>
    string? GetAssetPath(Guid videoId, string assetName);

    /// <summary>Bytes on disk for the video: published output plus the kept source.</summary>
    VideoFootprint Measure(Guid videoId);

    /// <summary>Removes everything stored for the video (output, source, scratch).</summary>
    void Delete(Guid videoId);

    /// <summary>Removes scratch directories, e.g. after a crash. Leaves directories for <paramref name="keep"/>.</summary>
    void CleanWorkDirectories(IReadOnlySet<Guid> keep);

    IEnumerable<Guid> ListPublishedVideoIds();

    IEnumerable<Guid> ListSourceVideoIds();

    /// <summary>Bytes held by uploads that are still in progress.</summary>
    long MeasureUploads();

    /// <summary>Bytes held by processing scratch directories.</summary>
    long MeasureWork();
}

public readonly record struct VideoFootprint(long OutputBytes, long SourceBytes)
{
    public long Total => OutputBytes + SourceBytes;
}
