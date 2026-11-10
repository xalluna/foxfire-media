using System.Net;
using Foxfire.IntegrationTests.Infrastructure;

namespace Foxfire.IntegrationTests.Web;

/// <summary>Uses a fake build output so the tests do not depend on running the web build.</summary>
public sealed class FakeWebRootFactory : FoxfireFactory
{
    public FakeWebRootFactory()
    {
        WebRoot = StorageRoot + "-web";
        Directory.CreateDirectory(Path.Combine(WebRoot, "admin"));
        Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));
        Directory.CreateDirectory(Path.Combine(WebRoot, ".vite"));
        File.WriteAllText(Path.Combine(WebRoot, "admin", "index.html"), "<!doctype html><div id=\"root\"></div>");
        File.WriteAllText(Path.Combine(WebRoot, "assets", "admin-abc123.js"), "console.log('admin')");
        File.WriteAllText(Path.Combine(WebRoot, ".vite", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(WebRoot, "secret.txt"), "not public");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(WebRoot!, recursive: true);
    }
}

public sealed class WebHostingTests(FakeWebRootFactory factory) : IClassFixture<FakeWebRootFactory>
{
    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : null;

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/")]
    [InlineData("/admin/videos/abc123")]
    [InlineData("/admin/storage")]
    public async Task Admin_routes_serve_the_spa_with_a_strict_unframable_policy(string path)
    {
        using var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"root\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        var csp = Header(response, "Content-Security-Policy")!;
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", csp, StringComparison.Ordinal);
        Assert.Equal("no-cache", Header(response, "Cache-Control"));
    }

    [Fact]
    public async Task Hashed_assets_are_served_immutably()
    {
        using var response = await factory.CreateClient().GetAsync("/assets/admin-abc123.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", Header(response, "Cache-Control"), StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Theory]
    [InlineData("/.vite/manifest.json")]
    [InlineData("/secret.txt")]
    [InlineData("/admin/index.html/../../secret.txt")]
    [InlineData("/assets/../secret.txt")]
    [InlineData("/assets/%2e%2e/secret.txt")]
    [InlineData("/wwwroot/secret.txt")]
    public async Task Nothing_else_in_the_web_root_is_reachable(string path)
    {
        using var response = await factory.CreateClient().GetAsync(path);

        var body = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("not public", body, StringComparison.Ordinal);
        Assert.NotEqual("{}", body);
    }

    [Fact]
    public async Task Root_redirects_to_the_admin_console()
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/", response.Headers.Location!.OriginalString);
    }
}
