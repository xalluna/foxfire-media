using Foxfire.Server.Security;

namespace Foxfire.Server.Streaming;

public sealed class StreamingOptions
{
    public const string Section = "Streaming";

    /// <summary>Lifetime of a playback token; the player refreshes well before it lapses.</summary>
    public TimeSpan PlaybackTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A viewing session cannot be refreshed beyond this, measured from when the embed was opened.</summary>
    public TimeSpan MaxSessionLength { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Extra lifetime for native-HLS players (which cannot swap tokens mid-stream) beyond the duration.</summary>
    public TimeSpan NativePlaybackGrace { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Issues the path-embedded tokens used by /stream (playlist, segments, poster).</summary>
public sealed class PlaybackTokens(TokenService tokens, TimeProvider time, Microsoft.Extensions.Options.IOptions<StreamingOptions> options)
{
    public const byte NativeFlag = 1;

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid videoId, Guid applicationId, int epoch, DateTimeOffset sessionStart, long? durationMs, bool native)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var sessionEnd = sessionStart + settings.MaxSessionLength;
        var lifetime = native
            ? TimeSpan.FromMilliseconds(durationMs ?? 0) + settings.NativePlaybackGrace
            : settings.PlaybackTokenLifetime;
        var expires = now + lifetime < sessionEnd ? now + lifetime : sessionEnd;

        var token = tokens.Issue(TokenPurpose.Playback, new TokenClaims(videoId, applicationId, epoch, expires)
        {
            SessionStart = sessionStart,
            Flags = native ? NativeFlag : (byte)0,
        });
        return (token, expires);
    }

    public bool SessionExpired(TokenClaims claims) =>
        time.GetUtcNow() - claims.SessionStart >= options.Value.MaxSessionLength;
}
