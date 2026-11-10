using System.Security.Cryptography;
using System.Text;

namespace Foxfire.Server.Applications;

/// <summary>
/// In-memory map of SHA-256(api key) → application. The raw keys are never held here.
/// API keys are high-entropy random strings, so a fast hash is sufficient for lookup.
/// </summary>
public sealed class ApiKeyRegistry
{
    public sealed record Entry(byte[] Hash, Guid ApplicationId, string ApplicationName);

    private volatile Entry[] _entries = [];

    public void Load(IEnumerable<Entry> entries) => _entries = [.. entries];

    public Entry? Find(string presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey) || presentedKey.Length > 1024)
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        Entry? match = null;

        // Compare against every entry without exiting early so timing does not depend on which entry matches.
        foreach (var entry in _entries)
        {
            if (CryptographicOperations.FixedTimeEquals(entry.Hash, hash))
            {
                match = entry;
            }
        }

        return match;
    }
}
