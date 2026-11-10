using System.Buffers.Text;
using System.Security.Cryptography;

namespace Foxfire.Server.Videos;

public static class VideoIds
{
    public const int PublicIdLength = 11;

    /// <summary>64 random bits, base64url (11 characters), like a YouTube id.</summary>
    public static string NewPublicId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(8));

    public static bool LooksLikePublicId(string? value) =>
        value is { Length: PublicIdLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public static class VideoRules
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 5000;
    public const int MaxExternalIdLength = 200;

    public static string? ValidateTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Title is required."
        : title.Length > MaxTitleLength ? $"Title must be at most {MaxTitleLength} characters."
        : null;

    public static string? ValidateDescription(string? description) =>
        description is { Length: > MaxDescriptionLength } ? $"Description must be at most {MaxDescriptionLength} characters." : null;

    public static string? ValidateExternalId(string? externalId) =>
        externalId is null ? null
        : externalId.Length is 0 or > MaxExternalIdLength || externalId.Any(c => c is < '!' or > '~')
            ? $"ExternalId must be 1-{MaxExternalIdLength} printable ASCII characters without spaces."
            : null;
}
