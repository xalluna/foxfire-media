using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Applications;

namespace Foxfire.IntegrationTests.Applications;

public sealed class ApiKeyAuthFactory : FoxfireFactory
{
    public ApiKeyAuthFactory()
    {
        AppAKey = AddApp("AppA", origins: ["https://a.example"], webhookUrl: "https://a.example/hooks");
        AppASecondary = NewKey();
        Settings["ApiKeysSecondary:AppA"] = AppASecondary;
        KnownSecrets.Add(AppASecondary);
        AppBKey = AddApp("AppB");
    }

    public string AppAKey { get; }
    public string AppASecondary { get; }
    public string AppBKey { get; }
}

public sealed class ApiKeyAuthTests(ApiKeyAuthFactory factory) : IClassFixture<ApiKeyAuthFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_header_is_401()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Wrong_key_is_401()
    {
        using var client = factory.CreateAppClient(FoxfireFactory.NewKey());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Each_key_identifies_its_own_application()
    {
        using var a = factory.CreateAppClient(factory.AppAKey);
        using var b = factory.CreateAppClient(factory.AppBKey);

        var meA = await a.GetFromJsonAsync<ApplicationInfoResponse>("/api/v1/me", Ct);
        var meB = await b.GetFromJsonAsync<ApplicationInfoResponse>("/api/v1/me", Ct);

        Assert.Equal("AppA", meA!.Name);
        Assert.Equal(["https://a.example"], meA.AllowedOrigins);
        Assert.Equal("https://a.example/hooks", meA.WebhookUrl);
        Assert.Equal("AppB", meB!.Name);
    }

    [Fact]
    public async Task Secondary_key_authenticates_as_the_same_application()
    {
        using var client = factory.CreateAppClient(factory.AppASecondary);

        var me = await client.GetFromJsonAsync<ApplicationInfoResponse>("/api/v1/me", Ct);

        Assert.Equal("AppA", me!.Name);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("X-Api-Key")]
    [InlineData("key")]
    public async Task Key_in_query_string_is_rejected_even_with_a_valid_header(string parameter)
    {
        using var client = factory.CreateAppClient(factory.AppAKey);

        var response = await client.GetAsync($"/api/v1/me?{parameter}={Uri.EscapeDataString(factory.AppAKey)}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Key_in_authorization_header_is_not_accepted()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AppAKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Multiple_key_headers_are_rejected()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.TryAddWithoutValidation("X-Api-Key", [factory.AppAKey, factory.AppBKey]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request, Ct)).StatusCode);
    }
}

public sealed class FailedAuthThrottleTests
{
    [Fact]
    public async Task Repeated_failures_from_one_client_are_throttled()
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        var key = factory.AddApp("AppA");
        using var bad = factory.CreateAppClient(FoxfireFactory.NewKey());
        using var good = factory.CreateAppClient(key);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await bad.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode);
        // The throttle is per client address, so even a valid key from the same address waits out the window.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await good.GetAsync("/api/v1/me", TestContext.Current.CancellationToken)).StatusCode);
    }
}
