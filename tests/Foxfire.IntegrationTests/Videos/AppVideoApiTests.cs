using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Storage;
using Foxfire.Server.Videos;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Videos;

public sealed class TwoAppFactory : FoxfireFactory
{
    public TwoAppFactory()
    {
        AppAKey = AddApp("AppA", origins: ["https://a.example"]);
        AppBKey = AddApp("AppB", origins: ["https://b.example"]);
    }

    public string AppAKey { get; }
    public string AppBKey { get; }
}

public sealed class AppVideoApiTests(TwoAppFactory factory) : IClassFixture<TwoAppFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Status + headers + body, with the per-request traceId removed.</summary>
    private static async Task<string> Fingerprint(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        body.Remove("traceId");
        var headers = string.Join(';', response.Content.Headers.Concat(response.Headers)
            .Where(h => h.Key is not ("Date" or "Content-Length"))
            .Select(h => $"{h.Key}={string.Join(',', h.Value)}").Order());
        return $"{(int)response.StatusCode}|{headers}|{body.ToJsonString()}";
    }

    [Fact]
    public async Task Another_apps_video_is_indistinguishable_from_a_missing_one()
    {
        var bVideo = await factory.SeedVideoAsync("AppB", externalId: "b-ext-1");
        using var a = factory.CreateAppClient(factory.AppAKey);
        var missing = VideoIds.NewPublicId();

        var requests = new (HttpMethod Method, string Owned, string Missing, HttpContent? Body)[]
        {
            (HttpMethod.Get, $"/api/v1/videos/{bVideo.PublicId}", $"/api/v1/videos/{missing}", null),
            (HttpMethod.Get, "/api/v1/videos/by-external-id/b-ext-1", "/api/v1/videos/by-external-id/nope", null),
            (HttpMethod.Patch, $"/api/v1/videos/{bVideo.PublicId}", $"/api/v1/videos/{missing}", JsonContent.Create(new { title = "pwned" })),
            (HttpMethod.Delete, $"/api/v1/videos/{bVideo.PublicId}", $"/api/v1/videos/{missing}", null),
        };

        foreach (var (method, owned, notThere, body) in requests)
        {
            using var other = await a.SendAsync(new HttpRequestMessage(method, owned) { Content = body }, Ct);
            using var absent = await a.SendAsync(new HttpRequestMessage(method, notThere) { Content = body is null ? null : JsonContent.Create(new { title = "pwned" }) }, Ct);

            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
            Assert.Equal(await Fingerprint(absent), await Fingerprint(other));
        }

        var untouched = await factory.FindVideoAsync(bVideo.Id);
        Assert.NotNull(untouched);
        Assert.Equal(bVideo.Title, untouched.Title);
        Assert.Equal(VideoStatus.Ready, untouched.Status);
    }

    [Fact]
    public async Task Listing_only_shows_the_callers_videos()
    {
        var mine = await factory.SeedVideoAsync("AppA");
        var theirs = await factory.SeedVideoAsync("AppB");
        using var a = factory.CreateAppClient(factory.AppAKey);

        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await a.GetFromJsonAsync<VideoPage>($"/api/v1/videos?limit=100{(cursor is null ? "" : "&cursor=" + cursor)}", Ct);
            ids.AddRange(page!.Items.Select(v => v.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Contains(mine.PublicId, ids);
        Assert.DoesNotContain(theirs.PublicId, ids);
    }

    [Fact]
    public async Task Owner_can_read_update_and_delete_its_video_and_files_are_removed()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        using var a = factory.CreateAppClient(factory.AppAKey);
        var storage = factory.Services.GetRequiredService<IMediaStorage>();
        Assert.NotNull(storage.GetAssetPath(video.Id, "index.m3u8"));

        var fetched = await a.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{video.PublicId}", Ct);
        Assert.Equal(video.Title, fetched!.Title);
        Assert.Equal("Ready", fetched.Status);

        var patch = await a.PatchAsJsonAsync($"/api/v1/videos/{video.PublicId}", new { title = "  Renamed  ", description = "d", externalId = "ext-own" }, Ct);
        var updated = await patch.Content.ReadFromJsonAsync<VideoResponse>(Ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Renamed", updated!.Title);
        Assert.Equal("ext-own", updated.ExternalId);

        var byExternal = await a.GetFromJsonAsync<VideoResponse>("/api/v1/videos/by-external-id/ext-own", Ct);
        Assert.Equal(video.PublicId, byExternal!.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/v1/videos/{video.PublicId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/v1/videos/{video.PublicId}", Ct)).StatusCode);
        Assert.Null(await factory.FindVideoAsync(video.Id));
        Assert.Null(storage.GetAssetPath(video.Id, "index.m3u8"));
        Assert.Null(storage.FindSource(video.Id));
    }

    [Fact]
    public async Task Duplicate_external_id_within_an_app_conflicts_but_is_fine_across_apps()
    {
        await factory.SeedVideoAsync("AppA", externalId: "shared-ext");
        var other = await factory.SeedVideoAsync("AppA");
        await factory.SeedVideoAsync("AppB", externalId: "shared-ext");
        using var a = factory.CreateAppClient(factory.AppAKey);

        var response = await a.PatchAsJsonAsync($"/api/v1/videos/{other.PublicId}", new { externalId = "shared-ext" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"title":""}""")]
    [InlineData("""{"externalId":"has space"}""")]
    public async Task Invalid_updates_are_rejected(string json)
    {
        var video = await factory.SeedVideoAsync("AppA");
        using var a = factory.CreateAppClient(factory.AppAKey);

        var response = await a.PatchAsync($"/api/v1/videos/{video.PublicId}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_videos_are_hidden()
    {
        var video = await factory.SeedVideoAsync("AppA", status: VideoStatus.Deleting);
        using var a = factory.CreateAppClient(factory.AppAKey);

        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/v1/videos/{video.PublicId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Paging_walks_every_video_exactly_once()
    {
        await using var isolated = new FoxfireFactory();
        await isolated.InitializeAsync();
        var key = isolated.AddApp("Pager");
        var seeded = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            seeded.Add((await isolated.SeedVideoAsync("Pager", status: i % 2 == 0 ? VideoStatus.Ready : VideoStatus.Queued)).PublicId);
        }

        using var client = isolated.CreateAppClient(key);
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await client.GetFromJsonAsync<VideoPage>($"/api/v1/videos?limit=2{(cursor is null ? "" : "&cursor=" + cursor)}", Ct);
            seen.AddRange(page!.Items.Select(v => v.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(seeded.Order(), seen.Order());
        Assert.Equal(seen.Count, seen.Distinct().Count());

        var ready = await client.GetFromJsonAsync<VideoPage>("/api/v1/videos?status=ready", Ct);
        Assert.Equal(3, ready!.Items.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/videos?cursor=%%%", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/videos?status=deleting", Ct)).StatusCode);
    }
}
