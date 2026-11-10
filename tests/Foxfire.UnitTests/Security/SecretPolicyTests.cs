using Foxfire.Server.Security;

namespace Foxfire.UnitTests.Security;

public sealed class SecretPolicyTests
{
    [Fact]
    public void Random_base64_key_is_accepted() =>
        Assert.Null(SecretPolicy.Validate(new Secret(TestKeys.New())));

    [Theory]
    [InlineData("FM_as4Hu8s02j", "at least 32")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789 ABCDEF", "whitespace")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "distinct")]
    [InlineData("abababababababababababababababababababab", "distinct")]
    [InlineData("replace-me-with-openssl-rand-base64-48-output-XYZ123", "placeholder")]
    [InlineData("CHANGEME-0123456789abcdefghijklmnopqrstuvwxyz", "placeholder")]
    public void Weak_or_placeholder_keys_are_rejected(string value, string expectedFragment)
    {
        var problem = SecretPolicy.Validate(new Secret(value));

        Assert.NotNull(problem);
        Assert.Contains(expectedFragment, problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlong_key_is_rejected()
    {
        var value = string.Concat(Enumerable.Repeat(TestKeys.New(), 10));

        Assert.Contains("at most", SecretPolicy.Validate(new Secret(value)));
    }
}
