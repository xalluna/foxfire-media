using System.Security.Cryptography;

namespace Foxfire.UnitTests;

internal static class TestKeys
{
    /// <summary>A fresh, policy-compliant random key (base64, 64 chars).</summary>
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}
