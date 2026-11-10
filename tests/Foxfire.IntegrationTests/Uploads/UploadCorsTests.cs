using System.Net;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.IntegrationTests.Videos;
using Foxfire.Server.Uploads;

namespace Foxfire.IntegrationTests.Uploads;

public sealed class UploadCorsTests(TwoAppFactory factory) : IClassFixture<TwoAppFactory>
{
    private async Task<HttpResponseMessage> PreflightAsync(string origin, string method = "PATCH")
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, UploadGrants.TusPath + "/somefile");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization, tus-resumable, upload-offset, content-type");
        return await factory.CreateClient().SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Theory]
    [InlineData("https://a.example")]
    [InlineData("https://b.example")]
    [InlineData(FoxfireFactory.PublicBaseUrl)]
    public async Task Preflight_from_a_configured_origin_is_allowed(string origin)
    {
        using var response = await PreflightAsync(origin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("PATCH", Header(response, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        Assert.Contains("Upload-Offset", Header(response, "Access-Control-Allow-Headers"), StringComparison.Ordinal);
        Assert.Contains("Authorization", Header(response, "Access-Control-Allow-Headers"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("https://a.example.evil.example")]
    public async Task Preflight_from_an_unknown_origin_gets_no_cors_grant(string origin)
    {
        using var response = await PreflightAsync(origin);

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task Browser_upload_from_the_owning_apps_origin_works_and_exposes_tus_headers()
    {
        using var app = factory.CreateAppClient(factory.AppAKey);
        var ticket = await TusClient.MintAsync(app, sizeBytes: 10);
        var tus = new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = "https://a.example" };

        using var create = await tus.CreateAsync(10);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal("https://a.example", Header(create, "Access-Control-Allow-Origin"));
        Assert.Contains("Location", Header(create, "Access-Control-Expose-Headers"), StringComparison.Ordinal);
        Assert.Contains("Upload-Offset", Header(create, "Access-Control-Expose-Headers"), StringComparison.Ordinal);

        var location = create.Headers.Location!.OriginalString;
        using var patch = await tus.PatchAsync(location, 0, new byte[10]);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
    }

    [Fact]
    public async Task Another_apps_origin_cannot_use_the_token_and_does_not_consume_it()
    {
        using var app = factory.CreateAppClient(factory.AppAKey);
        var ticket = await TusClient.MintAsync(app, sizeBytes: 10);

        using var foreign = await new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = "https://b.example" }.CreateAsync(10);
        using var unknown = await new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = "https://evil.example" }.CreateAsync(10);

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Null(Header(unknown, "Access-Control-Allow-Origin"));

        using var legit = await new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = "https://a.example" }.CreateAsync(10);
        Assert.Equal(HttpStatusCode.Created, legit.StatusCode);
    }

    [Fact]
    public async Task Continuing_an_upload_from_another_origin_is_refused()
    {
        using var app = factory.CreateAppClient(factory.AppAKey);
        var ticket = await TusClient.MintAsync(app, sizeBytes: 10);
        var location = await new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = "https://a.example" }.CreateOrThrowAsync(10);

        using var patch = await new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = "https://b.example" }.PatchAsync(location, 0, new byte[10]);

        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
    }

    [Fact]
    public async Task FoxfireMedias_own_origin_may_upload_for_any_app()
    {
        using var app = factory.CreateAppClient(factory.AppBKey);
        var ticket = await TusClient.MintAsync(app, sizeBytes: 10);

        using var create = await new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = FoxfireFactory.PublicBaseUrl }.CreateAsync(10);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }
}
