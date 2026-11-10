using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.IntegrationTests.Videos;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Foxfire.Server.Streaming;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Streaming;

public sealed class StreamTests(TwoAppFactory factory) : IClassFixture<TwoAppFactory>
{
    private async Task<string> PlaybackTokenAsync(Video video, bool native = false)
    {
        var app = await AppAsync(video.ApplicationId);
        return factory.Services.GetRequiredService<PlaybackTokens>()
            .Issue(video.Id, video.ApplicationId, app.TokenEpoch, factory.Time.GetUtcNow(), video.DurationMs, native).Token;
    }

    private async Task<Application> AppAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<Application>().AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private Task<HttpResponseMessage> GetAsync(string url, string? fetchSite = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (fetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return factory.CreateClient().SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : response.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : null;

    private static async Task<string> FingerprintAsync(HttpResponseMessage response) =>
        $"{(int)response.StatusCode}|{Header(response, "Content-Type")}|{Header(response, "Cache-Control")}|{await response.Content.ReadAsStringAsync()}";

    [Fact]
    public async Task Playlist_and_segments_are_served_with_protective_headers()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);

        using var playlist = await GetAsync($"/stream/{token}/index.m3u8", fetchSite: "same-origin", origin: "https://evil.example");
        using var segment = await GetAsync($"/stream/{token}/seg_00000.m4s", fetchSite: "same-origin");
        using var init = await GetAsync($"/stream/{token}/init.mp4");
        using var poster = await GetAsync($"/stream/{token}/poster.jpg");

        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", Header(playlist, "Content-Type"));
        Assert.Contains("seg_00000.m4s", await playlist.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("no-store", Header(playlist, "Cache-Control"));
        Assert.Equal("video/mp4", Header(segment, "Content-Type"));
        Assert.Contains("private", Header(segment, "Cache-Control"), StringComparison.Ordinal);
        Assert.Contains("max-age=900", Header(segment, "Cache-Control"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Equal("image/jpeg", Header(poster, "Content-Type"));

        foreach (var response in new[] { playlist, segment, init, poster })
        {
            Assert.Equal("same-origin", Header(response, "Cross-Origin-Resource-Policy"));
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
            Assert.Null(Header(response, "Content-Disposition"));
            Assert.Null(Header(response, "Access-Control-Allow-Origin"));
            Assert.Null(Header(response, "Accept-Ranges"));
        }
    }

    [Fact]
    public async Task Range_requests_are_not_honoured()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/stream/{token}/seg_00000.m4s");
        request.Headers.Range = new RangeHeaderValue(0, 9);

        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(256, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task Cross_site_fetches_are_refused(string site)
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);

        using var response = await GetAsync($"/stream/{token}/index.m3u8", fetchSite: site);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("..%2Findex.m3u8")]
    [InlineData("%2e%2e%2f%2e%2e%2fsources")]
    [InlineData("source.mp4")]
    [InlineData("INDEX.M3U8")]
    [InlineData("seg_1.m4s")]
    [InlineData("seg_00009.m4s")]
    public async Task Only_published_allow_listed_assets_resolve(string asset)
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);

        using var response = await GetAsync($"/stream/{token}/{asset}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Every_denial_looks_the_same()
    {
        var ready = await factory.SeedVideoAsync("AppA", withFiles: true);
        var processing = await factory.SeedVideoAsync("AppA", status: VideoStatus.Processing, withFiles: true);
        var good = await PlaybackTokenAsync(ready);
        var tokens = factory.Services.GetRequiredService<TokenService>();
        var wrongPurpose = tokens.Issue(TokenPurpose.Embed, new TokenClaims(ready.Id, ready.ApplicationId, 0, factory.Time.GetUtcNow().AddHours(1)));
        var wrongApp = tokens.Issue(TokenPurpose.Playback, new TokenClaims(ready.Id, await factory.AppIdAsync("AppB"), 0, factory.Time.GetUtcNow().AddHours(1)));

        var denials = new[]
        {
            $"/stream/{good[..^2]}xx/index.m3u8",
            $"/stream/{wrongPurpose}/index.m3u8",
            $"/stream/{wrongApp}/index.m3u8",
            $"/stream/{await PlaybackTokenAsync(processing)}/index.m3u8",
            $"/stream/{good}/source.mp4",
            "/stream/v1.nothing.here/index.m3u8",
        };

        var fingerprints = new List<string>();
        foreach (var url in denials)
        {
            using var response = await GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            fingerprints.Add(await FingerprintAsync(response));
        }

        Assert.Single(fingerprints.Distinct());
    }

    [Fact]
    public async Task Tokens_expire_and_deleted_videos_stop_streaming_immediately()
    {
        var expiring = await factory.SeedVideoAsync("AppA", withFiles: true);
        var deleted = await factory.SeedVideoAsync("AppA", withFiles: true);
        var expiringToken = await PlaybackTokenAsync(expiring);
        var deletedToken = await PlaybackTokenAsync(deleted);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"/stream/{deletedToken}/index.m3u8")).StatusCode); // warm the cache

        using (var app = factory.CreateAppClient(factory.AppAKey))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await app.DeleteAsync($"/api/v1/videos/{deleted.PublicId}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/stream/{deletedToken}/index.m3u8")).StatusCode);

        factory.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/stream/{expiringToken}/seg_00000.m4s")).StatusCode);
    }

    [Fact]
    public async Task Bumping_the_token_epoch_revokes_outstanding_tokens()
    {
        await using var isolated = new FoxfireFactory();
        await isolated.InitializeAsync();
        isolated.AddApp("AppA");
        var video = await isolated.SeedVideoAsync("AppA", withFiles: true);
        var token = isolated.Services.GetRequiredService<PlaybackTokens>().Issue(video.Id, video.ApplicationId, 0, isolated.Time.GetUtcNow(), null, false).Token;
        Assert.Equal(HttpStatusCode.OK, (await isolated.CreateClient().GetAsync($"/stream/{token}/index.m3u8")).StatusCode);

        await using (var scope = isolated.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<Application>()
                .Where(a => a.Id == video.ApplicationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.TokenEpoch, 1));
        }

        isolated.Services.GetRequiredService<VideoAccess>().Evict(video.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await isolated.CreateClient().GetAsync($"/stream/{token}/index.m3u8")).StatusCode);
    }

    [Fact]
    public async Task Refresh_slides_the_session_but_never_past_its_cap()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);
        using var client = factory.CreateClient();
        var start = factory.Time.GetUtcNow();

        var refreshes = 0;
        while (true)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(14));
            using var response = await client.PostAsync($"/stream/{token}/refresh", null);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                break;
            }

            var refreshed = await response.Content.ReadFromJsonAsync<PlaybackRefreshResponse>();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(refreshed!.ManifestUrl)).StatusCode);
            Assert.True(refreshed.ExpiresAt <= start + TimeSpan.FromHours(12));
            token = refreshed.Token;
            refreshes++;
            Assert.True(refreshes < 100, "refresh never stopped");
        }

        Assert.InRange(factory.Time.GetUtcNow() - start, TimeSpan.FromHours(11.7), TimeSpan.FromHours(12.3));
    }

    [Fact]
    public async Task Native_mode_refresh_covers_the_whole_video()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);

        using var response = await factory.CreateClient().PostAsync($"/stream/{token}/refresh?mode=native", null);
        var refreshed = await response.Content.ReadFromJsonAsync<PlaybackRefreshResponse>();

        Assert.Equal(factory.Time.GetUtcNow() + TimeSpan.FromMilliseconds(12_000) + TimeSpan.FromHours(1), refreshed!.ExpiresAt);
    }

    [Fact]
    public async Task Cross_site_refresh_is_refused()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/stream/{token}/refresh");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Stream_tokens_never_reach_the_logs()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var token = await PlaybackTokenAsync(video);

        await GetAsync($"/stream/{token}/index.m3u8");
        await GetAsync($"/stream/{token}/seg_00000.m4s");

        Assert.DoesNotContain(token, factory.Logs.All, StringComparison.Ordinal);
        Assert.Contains("/stream/***/index.m3u8", factory.Logs.All, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/data/media/videos")]
    [InlineData("/sources")]
    [InlineData("/videos")]
    [InlineData("/uploads")]
    [InlineData("/stream")]
    [InlineData("/appsettings.json")]
    [InlineData("/config/appsettings.json")]
    public async Task Nothing_on_disk_is_reachable_outside_the_stream_endpoint(string path) =>
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(path)).StatusCode);
}
