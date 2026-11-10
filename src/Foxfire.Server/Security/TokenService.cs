using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Foxfire.Server.Security;

public enum TokenPurpose : byte
{
    Embed = 1,
    Playback = 2,
    Upload = 3,
    Poster = 4,
}

/// <summary>
/// What a token asserts. Fields a purpose does not use are left at their defaults.
/// Consumers must still check <see cref="Epoch"/> against the application's current token epoch
/// and the video's ownership/status in the database.
/// </summary>
public sealed record TokenClaims(Guid VideoId, Guid ApplicationId, int Epoch, DateTimeOffset ExpiresAt)
{
    /// <summary>Playback: when the viewing session began (bounds the total refresh lifetime).</summary>
    public DateTimeOffset SessionStart { get; init; }

    /// <summary>Upload: the grant this token belongs to; re-minting a grant invalidates older tokens.</summary>
    public Guid GrantId { get; init; }

    /// <summary>Upload: the most bytes this token may upload.</summary>
    public long MaxBytes { get; init; }

    /// <summary>Purpose-specific flags (e.g. playback issued for native HLS).</summary>
    public byte Flags { get; init; }
}

/// <summary>
/// Compact signed tokens: <c>v1.&lt;base64url payload&gt;.&lt;base64url mac&gt;</c>.
/// The MAC is HMAC-SHA256 (truncated to 128 bits) under a per-purpose key derived with HKDF from
/// the root signing key, so a token minted for one purpose never validates for another.
/// </summary>
public sealed class TokenService
{
    private const string Prefix = "v1.";
    private const int MacBytes = 16;
    private const int PayloadBytes = 1 + 16 + 16 + 4 + 8 + 8 + 16 + 8 + 1;
    private const int MaxTokenLength = 512;

    private readonly TimeProvider _time;
    private readonly Dictionary<TokenPurpose, byte[]> _current;
    private readonly Dictionary<TokenPurpose, byte[]>? _previous;

    public TokenService(SigningKeys keys, TimeProvider time)
    {
        _time = time;
        _current = DeriveAll(keys.Current);
        _previous = keys.Previous is null ? null : DeriveAll(keys.Previous);
    }

    public string Issue(TokenPurpose purpose, TokenClaims claims)
    {
        Span<byte> payload = stackalloc byte[PayloadBytes];
        Write(payload, purpose, claims);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_current[purpose], payload, mac);

        return $"{Prefix}{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(mac[..MacBytes])}";
    }

    public bool TryValidate(TokenPurpose purpose, string? token, out TokenClaims claims)
    {
        claims = default!;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = token.AsSpan(Prefix.Length);
        var dot = body.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        var payloadText = body[..dot];
        var macText = body[(dot + 1)..];
        Span<byte> payload = stackalloc byte[PayloadBytes];
        Span<byte> presentedMac = stackalloc byte[MacBytes];
        if (!Base64Url.IsValid(payloadText) || !Base64Url.IsValid(macText) ||
            !Base64Url.TryDecodeFromChars(payloadText, payload, out var payloadLength) || payloadLength != PayloadBytes ||
            !Base64Url.TryDecodeFromChars(macText, presentedMac, out var macLength) || macLength != MacBytes)
        {
            return false;
        }

        // Require canonical encoding: the MAC's last base64 character carries unused bits, and accepting
        // variants would make one token spellable several ways (and defeat exact-match bookkeeping).
        if (!macText.SequenceEqual(Base64Url.EncodeToString(presentedMac)) ||
            !payloadText.SequenceEqual(Base64Url.EncodeToString(payload)))
        {
            return false;
        }

        if (!MacMatches(_current[purpose], payload, presentedMac) &&
            !(_previous is not null && MacMatches(_previous[purpose], payload, presentedMac)))
        {
            return false;
        }

        if (payload[0] != (byte)purpose)
        {
            return false;
        }

        var parsed = Read(payload);
        if (parsed.ExpiresAt <= _time.GetUtcNow())
        {
            return false;
        }

        claims = parsed;
        return true;
    }

    private static bool MacMatches(byte[] key, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> presented)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(key, payload, expected);
        return CryptographicOperations.FixedTimeEquals(expected[..MacBytes], presented);
    }

    private static Dictionary<TokenPurpose, byte[]> DeriveAll(byte[] root) =>
        Enum.GetValues<TokenPurpose>().ToDictionary(
            p => p,
            p => HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, salt: [], info: Encoding.ASCII.GetBytes($"foxfire/{p.ToString().ToLowerInvariant()}/v1")));

    private static void Write(Span<byte> buffer, TokenPurpose purpose, TokenClaims claims)
    {
        buffer[0] = (byte)purpose;
        claims.VideoId.TryWriteBytes(buffer[1..17], bigEndian: true, out _);
        claims.ApplicationId.TryWriteBytes(buffer[17..33], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(buffer[33..37], claims.Epoch);
        BinaryPrimitives.WriteInt64BigEndian(buffer[37..45], claims.ExpiresAt.ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt64BigEndian(buffer[45..53], claims.SessionStart == default ? 0 : claims.SessionStart.ToUnixTimeSeconds());
        claims.GrantId.TryWriteBytes(buffer[53..69], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(buffer[69..77], claims.MaxBytes);
        buffer[77] = claims.Flags;
    }

    private static TokenClaims Read(ReadOnlySpan<byte> buffer)
    {
        var sessionStart = BinaryPrimitives.ReadInt64BigEndian(buffer[45..53]);
        return new TokenClaims(
            new Guid(buffer[1..17], bigEndian: true),
            new Guid(buffer[17..33], bigEndian: true),
            BinaryPrimitives.ReadInt32BigEndian(buffer[33..37]),
            DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(BinaryPrimitives.ReadInt64BigEndian(buffer[37..45]), 0, 253402300799)))
        {
            SessionStart = sessionStart == 0 ? default : DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(sessionStart, 0, 253402300799)),
            GrantId = new Guid(buffer[53..69], bigEndian: true),
            MaxBytes = BinaryPrimitives.ReadInt64BigEndian(buffer[69..77]),
            Flags = buffer[77],
        };
    }
}
