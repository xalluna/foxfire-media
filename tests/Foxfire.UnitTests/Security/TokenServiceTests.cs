using Foxfire.Server.Applications;
using Foxfire.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Foxfire.UnitTests.Security;

public sealed class TokenServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    private static readonly ApplicationCatalog EmptyCatalog = ApplicationCatalog.Load(new ConfigurationBuilder().Build(), false);

    private static SigningKeys Keys(string current, string? previous = null) =>
        SigningKeys.Load(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SigningKeys.KeyPath] = current,
                [SigningKeys.PreviousKeyPath] = previous,
            }).Build(),
            EmptyCatalog);

    private TokenService Service(string current, string? previous = null) => new(Keys(current, previous), _time);

    private TokenClaims Claims(TimeSpan ttl) => new(Guid.NewGuid(), Guid.NewGuid(), 3, _time.GetUtcNow() + ttl)
    {
        SessionStart = _time.GetUtcNow(),
        GrantId = Guid.NewGuid(),
        MaxBytes = 5_000_000_000,
        Flags = 1,
    };

    [Fact]
    public void Round_trips_every_field()
    {
        var tokens = Service(TestKeys.New());
        var claims = Claims(TimeSpan.FromMinutes(15));

        var token = tokens.Issue(TokenPurpose.Upload, claims);

        Assert.StartsWith("v1.", token, StringComparison.Ordinal);
        Assert.True(token.Length < 200);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('+', token);
        Assert.True(tokens.TryValidate(TokenPurpose.Upload, token, out var parsed));
        Assert.Equal(claims, parsed);
    }

    [Fact]
    public void Any_single_character_change_invalidates_the_token()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.";
        var tokens = Service(TestKeys.New());

        // Several tokens so every position sees a variety of original characters (incl. padding-bit-only changes).
        for (var round = 0; round < 5; round++)
        {
            var token = tokens.Issue(TokenPurpose.Embed, Claims(TimeSpan.FromHours(1)));
            for (var i = 0; i < token.Length; i++)
            {
                foreach (var replacement in alphabet)
                {
                    if (replacement == token[i])
                    {
                        continue;
                    }

                    var chars = token.ToCharArray();
                    chars[i] = replacement;
                    Assert.False(tokens.TryValidate(TokenPurpose.Embed, new string(chars), out _), $"Tampering at index {i} was not detected.");
                }
            }
        }
    }

    [Theory]
    [InlineData(TokenPurpose.Embed, TokenPurpose.Playback)]
    [InlineData(TokenPurpose.Playback, TokenPurpose.Embed)]
    [InlineData(TokenPurpose.Upload, TokenPurpose.Playback)]
    [InlineData(TokenPurpose.Poster, TokenPurpose.Playback)]
    public void A_token_for_one_purpose_never_validates_for_another(TokenPurpose issued, TokenPurpose checkedAs)
    {
        var tokens = Service(TestKeys.New());
        var token = tokens.Issue(issued, Claims(TimeSpan.FromHours(1)));

        Assert.False(tokens.TryValidate(checkedAs, token, out _));
    }

    [Fact]
    public void Expires_exactly_at_its_expiry()
    {
        var tokens = Service(TestKeys.New());
        var token = tokens.Issue(TokenPurpose.Playback, Claims(TimeSpan.FromMinutes(15)));

        _time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.True(tokens.TryValidate(TokenPurpose.Playback, token, out _));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tokens.TryValidate(TokenPurpose.Playback, token, out _));
    }

    [Fact]
    public void Previous_key_still_verifies_after_rotation_but_new_tokens_use_the_new_key()
    {
        var oldKey = TestKeys.New();
        var newKey = TestKeys.New();
        var before = Service(oldKey);
        var issuedBefore = before.Issue(TokenPurpose.Embed, Claims(TimeSpan.FromHours(1)));

        var rotated = Service(newKey, previous: oldKey);
        var issuedAfter = rotated.Issue(TokenPurpose.Embed, Claims(TimeSpan.FromHours(1)));

        Assert.True(rotated.TryValidate(TokenPurpose.Embed, issuedBefore, out _));
        Assert.True(rotated.TryValidate(TokenPurpose.Embed, issuedAfter, out _));
        Assert.False(before.TryValidate(TokenPurpose.Embed, issuedAfter, out _));
        Assert.False(Service(newKey).TryValidate(TokenPurpose.Embed, issuedBefore, out _));
    }

    [Fact]
    public void A_different_key_rejects_the_token() =>
        Assert.False(Service(TestKeys.New()).TryValidate(TokenPurpose.Embed, Service(TestKeys.New()).Issue(TokenPurpose.Embed, Claims(TimeSpan.FromHours(1))), out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.")]
    [InlineData("v1..")]
    [InlineData("v2.abc.def")]
    [InlineData("v1.!!!.@@@")]
    [InlineData("v1.AAAA.AAAA")]
    [InlineData("v1.AAAA")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJ2aWQiOjF9.")]
    public void Malformed_tokens_are_rejected_without_throwing(string? token) =>
        Assert.False(Service(TestKeys.New()).TryValidate(TokenPurpose.Embed, token, out _));

    [Fact]
    public void Overlong_tokens_are_rejected() =>
        Assert.False(Service(TestKeys.New()).TryValidate(TokenPurpose.Embed, "v1." + new string('A', 5000) + ".AAAA", out _));
}

public sealed class SigningKeysTests
{
    private static FoxfireConfigurationException Fails(Dictionary<string, string?> settings) =>
        Assert.Throws<FoxfireConfigurationException>(() =>
            SigningKeys.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                ApplicationCatalog.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), false)));

    [Fact]
    public void Missing_key_is_refused() =>
        Assert.Contains(Fails([]).Problems, p => p.Contains("Signing__Key is not set", StringComparison.Ordinal));

    [Fact]
    public void Non_base64_key_is_refused() =>
        Assert.Contains(Fails(new() { [SigningKeys.KeyPath] = "this-is-not-base64-but-long-enough-0123456789!" }).Problems,
            p => p.Contains("base64", StringComparison.Ordinal));

    [Fact]
    public void Reusing_an_api_key_as_the_signing_key_is_refused()
    {
        var key = TestKeys.New();

        var ex = Fails(new() { [SigningKeys.KeyPath] = key, ["Applications:0:Name"] = "AppA", ["ApiKeys:AppA"] = key });

        Assert.Contains(ex.Problems, p => p.Contains("must not be the same value as an application API key", StringComparison.Ordinal));
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Signing_key_in_a_json_file_is_refused()
    {
        var key = TestKeys.New();
        var path = Path.Combine(Path.GetTempPath(), $"foxfire-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$$"""{"Signing":{"Key":"{{{key}}}"}}""");
        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
            var ex = Assert.Throws<FoxfireConfigurationException>(() =>
                SigningKeys.Load(configuration, ApplicationCatalog.Load(new ConfigurationBuilder().Build(), false)));

            Assert.Contains(ex.Problems, p => p.Contains("configuration file", StringComparison.Ordinal));
            Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
