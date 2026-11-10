using System.Text.Json;
using Foxfire.Server.Security;

namespace Foxfire.UnitTests.Security;

public sealed class SecretTests
{
    private sealed record Holder(string Name, Secret Key);

    [Fact]
    public void ToString_and_interpolation_are_redacted()
    {
        var value = TestKeys.New();
        var secret = new Secret(value);

        Assert.Equal("[redacted]", secret.ToString());
        Assert.DoesNotContain(value, $"key={secret}", StringComparison.Ordinal);
    }

    [Fact]
    public void Records_holding_a_secret_do_not_print_it()
    {
        var value = TestKeys.New();
        var holder = new Holder("app", new Secret(value));

        Assert.DoesNotContain(value, holder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_serialization_throws_even_when_nested()
    {
        var holder = new Holder("app", new Secret(TestKeys.New()));

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(holder));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(holder.Key));
    }

    [Fact]
    public void Reveal_returns_the_value()
    {
        var value = TestKeys.New();

        Assert.Equal(value, new Secret(value).Reveal());
    }
}
