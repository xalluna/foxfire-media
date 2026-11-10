using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foxfire.Server.Security;

/// <summary>
/// Holds a secret value (API key, signing key, bootstrap password). It never renders itself:
/// <see cref="ToString"/> is redacted, JSON serialization throws, and the debugger shows nothing.
/// The raw value is only reachable through <see cref="Reveal"/>, which call sites must use deliberately.
/// </summary>
[DebuggerDisplay("[redacted]")]
[JsonConverter(typeof(SecretJsonConverter))]
public sealed class Secret
{
    private readonly string _value;

    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public int Length => _value.Length;

    public string Reveal() => _value;

    public byte[] RevealUtf8() => Encoding.UTF8.GetBytes(_value);

    /// <summary>SHA-256 of the UTF-8 value. Used for in-memory lookups and duplicate detection only.</summary>
    public byte[] Sha256() => SHA256.HashData(RevealUtf8());

    public override string ToString() => "[redacted]";

    private sealed class SecretJsonConverter : JsonConverter<Secret>
    {
        public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Secrets are never deserialized from JSON.");

        public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Secrets are never serialized.");
    }
}
