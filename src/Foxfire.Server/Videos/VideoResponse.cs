namespace Foxfire.Server.Videos;

/// <summary>A video as an application sees it. Internal ids, paths and upload grants are never included.</summary>
public sealed record VideoResponse(
    string Id,
    string? ExternalId,
    string Title,
    string? Description,
    string Status,
    long? DurationMs,
    int? Width,
    int? Height,
    long SizeBytes,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt)
{
    public static VideoResponse From(Video v) => new(
        v.PublicId,
        v.ExternalId,
        v.Title,
        v.Description,
        v.Status.ToString(),
        v.DurationMs,
        v.Width,
        v.Height,
        v.StorageBytes,
        v.Status is VideoStatus.Failed or VideoStatus.UploadFailed ? v.ErrorMessage : null,
        v.CreatedAt,
        v.ReadyAt);
}

public sealed record VideoPage(IReadOnlyList<VideoResponse> Items, string? NextCursor);

public sealed record UpdateVideoRequest(string? Title, string? Description, string? ExternalId);
