using Foxfire.Server.Applications;

namespace Foxfire.UnitTests.Applications;

public sealed class OriginRulesTests
{
    [Theory]
    [InlineData("https://foxfire.alluna.dev", "https://foxfire.alluna.dev")]
    [InlineData("https://Foxfire.Alluna.dev/", "https://foxfire.alluna.dev")]
    [InlineData("https://example.com:443", "https://example.com")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    public void Valid_origins_are_normalized(string input, string expected) =>
        Assert.Equal(expected, OriginRules.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("foxfire.alluna.dev")]
    [InlineData("https://*.alluna.dev")]
    [InlineData("https://foxfire.alluna.dev/embed")]
    [InlineData("https://foxfire.alluna.dev?x=1")]
    [InlineData("https://user:pass@foxfire.alluna.dev")]
    [InlineData("ftp://foxfire.alluna.dev")]
    [InlineData("javascript:alert(1)")]
    public void Invalid_origins_are_rejected(string input) =>
        Assert.Null(OriginRules.Normalize(input));
}
