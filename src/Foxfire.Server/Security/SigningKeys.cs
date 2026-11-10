using Foxfire.Server.Applications;

namespace Foxfire.Server.Security;

/// <summary>
/// The root key that signs every embed, playback, upload and poster token
/// (<c>Signing__Key</c>, base64, at least 32 bytes) plus an optional previous key accepted for
/// verification only, so the key can be rotated without breaking tokens already handed out.
/// </summary>
public sealed class SigningKeys
{
    public const string KeyPath = "Signing:Key";
    public const string PreviousKeyPath = "Signing:PreviousKey";
    public const int MinKeyBytes = 32;

    private SigningKeys(byte[] current, byte[]? previous)
    {
        Current = current;
        Previous = previous;
    }

    public byte[] Current { get; }

    public byte[]? Previous { get; }

    public override string ToString() => "SigningKeys [redacted]";

    public static SigningKeys Load(IConfiguration configuration, ApplicationCatalog catalog)
    {
        var errors = SecretSourceGuard.FindViolations(configuration, [KeyPath, PreviousKeyPath]).ToList();

        var current = Decode(configuration[KeyPath], "Signing__Key", required: true, errors);
        var previous = Decode(configuration[PreviousKeyPath], "Signing__PreviousKey", required: false, errors);

        var apiKeyHashes = catalog.Applications
            .SelectMany(a => new[] { a.ApiKey, a.SecondaryApiKey })
            .OfType<Secret>()
            .Select(s => Convert.ToHexString(s.Sha256()))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (raw, name) in new[] { (configuration[KeyPath], "Signing__Key"), (configuration[PreviousKeyPath], "Signing__PreviousKey") })
        {
            if (!string.IsNullOrEmpty(raw) && apiKeyHashes.Contains(Convert.ToHexString(new Secret(raw).Sha256())))
            {
                errors.Add($"{name} must not be the same value as an application API key.");
            }
        }

        if (errors.Count > 0)
        {
            throw new FoxfireConfigurationException(errors);
        }

        return new SigningKeys(current!, previous);
    }

    private static byte[]? Decode(string? raw, string name, bool required, List<string> errors)
    {
        if (string.IsNullOrEmpty(raw))
        {
            if (required)
            {
                errors.Add($"{name} is not set. Generate one with `openssl rand -base64 48`.");
            }

            return null;
        }

        if (SecretPolicy.Validate(new Secret(raw)) is { } problem)
        {
            errors.Add($"{name} {problem}.");
            return null;
        }

        var buffer = new byte[raw.Length];
        if (!Convert.TryFromBase64String(raw, buffer, out var written))
        {
            errors.Add($"{name} must be base64 (generate one with `openssl rand -base64 48`).");
            return null;
        }

        if (written < MinKeyBytes)
        {
            errors.Add($"{name} must decode to at least {MinKeyBytes} bytes.");
            return null;
        }

        return buffer[..written];
    }
}
