namespace Foxfire.Server.Security;

/// <summary>Strength rules shared by API keys and other configured secrets. Messages never include the value.</summary>
public static class SecretPolicy
{
    public const int MinLength = 32;
    public const int MaxLength = 512;
    public const int MinDistinctChars = 16;

    private static readonly string[] PlaceholderMarkers =
    [
        "changeme", "change-me", "change_me", "replaceme", "replace-me", "replace_me",
        "placeholder", "example", "your-key", "your_key", "secret-here", "todo",
    ];

    /// <returns>A human-readable problem description, or null when the secret is acceptable.</returns>
    public static string? Validate(Secret secret)
    {
        var value = secret.Reveal();

        if (value.Length < MinLength)
        {
            return $"must be at least {MinLength} characters (generate one with `openssl rand -base64 48`)";
        }

        if (value.Length > MaxLength)
        {
            return $"must be at most {MaxLength} characters";
        }

        foreach (var c in value)
        {
            if (c is < '!' or > '~')
            {
                return "must contain only printable ASCII characters and no whitespace";
            }
        }

        if (value.Distinct().Count() < MinDistinctChars)
        {
            return $"must contain at least {MinDistinctChars} distinct characters";
        }

        var lower = value.ToLowerInvariant();
        if (PlaceholderMarkers.Any(lower.Contains))
        {
            return "looks like a placeholder value; generate a real one with `openssl rand -base64 48`";
        }

        return null;
    }
}
