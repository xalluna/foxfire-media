using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Data;
using Foxfire.Server.Embed;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Embed;

public sealed class EmbedFactory : FoxfireFactory
{
    public EmbedFactory()
    {
        AppAKey = AddApp("AppA", origins: ["https://a.example", "https://www.a.example"]);
        AppBKey = AddApp("AppB", origins: ["https://b.example"]);
        NoOriginsKey = AddApp("NoOrigins");
    }

    public string AppAKey { get; }
    public string AppBKey { get; }
    public string NoOriginsKey { get; }
}

public sealed partial class EmbedTests(EmbedFactory factory) : IClassFixture<EmbedFactory>
{
    [GeneratedRegex("<script type=\"application/json\" id=\"boot\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex BootPattern();

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : null;

    private async Task<EmbedResponse> MintAsync(string key, string publicId, int? ttl = null)
    {
        using var app = factory.CreateAppClient(key);
        var response = await app.PostAsJsonAsync($"/api/v1/videos/{publicId}/embed", new { ttlSeconds = ttl });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EmbedResponse>())!;
    }

    private static string PathOf(string absolute) => new Uri(absolute).PathAndQuery;

    [Fact]
    public async Task Embed_page_is_framable_only_by_the_owning_apps_origins_and_boots_the_player()
    {
        var video = await factory.SeedVideoAsync("AppA", title: "Boss fight </script><script>alert(1)</script>", withFiles: true);
        var embed = await MintAsync(factory.AppAKey, video.PublicId);

        Assert.StartsWith(FoxfireFactory.PublicBaseUrl + "/embed/v1.", embed.EmbedUrl, StringComparison.Ordinal);
        using var page = await factory.CreateClient().GetAsync(PathOf(embed.EmbedUrl) + "?autoplay=true&t=30");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var csp = Header(page, "Content-Security-Policy")!;
        Assert.EndsWith("frame-ancestors https://a.example https://www.a.example", csp, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.Equal("no-store", Header(page, "Cache-Control"));
        Assert.Equal("no-referrer", Header(page, "Referrer-Policy"));
        Assert.Null(Header(page, "X-Frame-Options"));

        // The hostile title is escaped in <title> and cannot break out of the boot JSON.
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        var bootJson = BootPattern().Match(html).Groups[1].Value;
        var boot = JsonSerializer.Deserialize<EmbedBoot>(bootJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(video.Title, boot.Title);
        Assert.True(boot.Autoplay);
        Assert.Equal(30, boot.Start);
        Assert.True(boot.Controls);
        Assert.Equal(["https://a.example", "https://www.a.example"], boot.ParentOrigins); // who may drive the player
        Assert.DoesNotContain(factory.AppAKey, html, StringComparison.Ordinal);

        // The boot manifest URL really streams.
        using var playlist = await factory.CreateClient().GetAsync(boot.ManifestUrl);
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
    }

    [Theory]
    [InlineData("?controls=0", false)]
    [InlineData("?controls=false", false)]
    [InlineData("?controls=1", true)]
    [InlineData("", true)]
    public async Task Built_in_controls_can_be_hidden_for_apps_with_their_own_ui(string query, bool expected)
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var embed = await MintAsync(factory.AppAKey, video.PublicId);

        var html = await factory.CreateClient().GetStringAsync(PathOf(embed.EmbedUrl) + query);
        var boot = JsonSerializer.Deserialize<EmbedBoot>(BootPattern().Match(html).Groups[1].Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(expected, boot.Controls);
    }

    [Fact]
    public async Task An_app_cannot_mint_an_embed_for_another_apps_video()
    {
        var bVideo = await factory.SeedVideoAsync("AppB", withFiles: true);
        using var appA = factory.CreateAppClient(factory.AppAKey);

        var stolen = await appA.PostAsJsonAsync($"/api/v1/videos/{bVideo.PublicId}/embed", new { });
        var missing = await appA.PostAsJsonAsync($"/api/v1/videos/{VideoIds.NewPublicId()}/embed", new { });

        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_copied_embed_url_is_still_locked_to_its_owners_origins()
    {
        var bVideo = await factory.SeedVideoAsync("AppB", withFiles: true);
        var embed = await MintAsync(factory.AppBKey, bVideo.PublicId);

        // App A copies B's URL into its own page: the browser enforces B's frame-ancestors.
        using var page = await factory.CreateClient().GetAsync(PathOf(embed.EmbedUrl));

        Assert.EndsWith("frame-ancestors https://b.example", Header(page, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.DoesNotContain("a.example", Header(page, "Content-Security-Policy"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apps_without_declared_origins_cannot_embed()
    {
        var video = await factory.SeedVideoAsync("NoOrigins", withFiles: true);
        using var app = factory.CreateAppClient(factory.NoOriginsKey);

        var response = await app.PostAsJsonAsync($"/api/v1/videos/{video.PublicId}/embed", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Tampered_expired_and_deleted_embeds_are_unavailable_and_unframable()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var deletedVideo = await factory.SeedVideoAsync("AppA", withFiles: true);
        var embed = await MintAsync(factory.AppAKey, video.PublicId, ttl: 300);
        var deleted = await MintAsync(factory.AppAKey, deletedVideo.PublicId);
        using (var app = factory.CreateAppClient(factory.AppAKey))
        {
            await app.DeleteAsync($"/api/v1/videos/{deletedVideo.PublicId}");
        }

        var tampered = PathOf(embed.EmbedUrl)[..^3] + "AAA";
        var client = factory.CreateClient();
        foreach (var path in new[] { tampered, PathOf(deleted.EmbedUrl), "/embed/not-a-token" })
        {
            using var page = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
            Assert.Contains("frame-ancestors 'none'", Header(page, "Content-Security-Policy"), StringComparison.Ordinal);
        }

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        using var expired = await client.GetAsync(PathOf(embed.EmbedUrl));
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task Lifetime_is_clamped()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);

        var tooShort = await MintAsync(factory.AppAKey, video.PublicId, ttl: 1);
        var tooLong = await MintAsync(factory.AppAKey, video.PublicId, ttl: 10_000_000);
        var standard = await MintAsync(factory.AppAKey, video.PublicId);

        var now = factory.Time.GetUtcNow();
        Assert.Equal(now.AddMinutes(5), tooShort.ExpiresAt);
        Assert.Equal(now.AddHours(24), tooLong.ExpiresAt);
        Assert.Equal(now.AddHours(1), standard.ExpiresAt);
    }

    [Fact]
    public async Task Processing_videos_show_a_waiting_page_with_the_same_framing_rules()
    {
        var video = await factory.SeedVideoAsync("AppA", status: VideoStatus.Processing);
        var embed = await MintAsync(factory.AppAKey, video.PublicId);

        using var page = await factory.CreateClient().GetAsync(PathOf(embed.EmbedUrl));
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("still processing", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"boot\"", html, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors https://a.example", Header(page, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.Null(embed.PosterUrl);
    }

    [Fact]
    public async Task Posters_are_cross_origin_images_bound_to_the_video()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        var embed = await MintAsync(factory.AppAKey, video.PublicId);

        using var poster = await factory.CreateClient().GetAsync(PathOf(embed.PosterUrl!));

        Assert.Equal(HttpStatusCode.OK, poster.StatusCode);
        Assert.Equal("image/jpeg", Header(poster, "Content-Type"));
        Assert.Equal("cross-origin", Header(poster, "Cross-Origin-Resource-Policy"));

        // A poster token is not a playback token and vice versa.
        var posterToken = PathOf(embed.PosterUrl!).Split('/')[2];
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/stream/{posterToken}/index.m3u8")).StatusCode);
        var embedToken = PathOf(embed.EmbedUrl).Split('/')[2];
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/poster/{embedToken}")).StatusCode);
    }

    [Fact]
    public async Task Revoking_the_app_epoch_kills_outstanding_embed_urls()
    {
        await using var isolated = new FoxfireFactory();
        await isolated.InitializeAsync();
        var key = isolated.AddApp("AppA", origins: ["https://a.example"]);
        var video = await isolated.SeedVideoAsync("AppA", withFiles: true);
        using var app = isolated.CreateAppClient(key);
        var embed = (await (await app.PostAsJsonAsync($"/api/v1/videos/{video.PublicId}/embed", new { })).Content.ReadFromJsonAsync<EmbedResponse>())!;

        await using (var scope = isolated.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<Server.Applications.Application>()
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TokenEpoch, a => a.TokenEpoch + 1));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await isolated.CreateClient().GetAsync(PathOf(embed.EmbedUrl))).StatusCode);
    }

    [Fact]
    public async Task The_player_script_is_referenced_from_the_vite_manifest_when_present()
    {
        await using var withWeb = new FoxfireFactory();
        await withWeb.InitializeAsync();
        var key = withWeb.AddApp("AppA", origins: ["https://a.example"]);
        var webRoot = Path.Combine(withWeb.StorageRoot + "-web");
        Directory.CreateDirectory(webRoot);
        // The real shape: the player is a chunk shared with the admin console, with its own stylesheet.
        await File.WriteAllTextAsync(Path.Combine(webRoot, EmbedAssets.ManifestFileName), """
            {
              "_player-p1.js": { "file": "assets/player-p1.js", "name": "player", "css": ["assets/player-p2.css"], "imports": ["_hls-h1.js"] },
              "_hls-h1.js": { "file": "assets/hls-h1.js" },
              "admin/index.html": { "file": "assets/admin-a1.js", "isEntry": true, "imports": ["_player-p1.js"], "css": ["assets/admin-a2.css"] },
              "src/embed/main.ts": { "file": "assets/embed-abc123.js", "isEntry": true, "imports": ["_player-p1.js"], "css": ["assets/embed-def456.css"] }
            }
            """);
        withWeb.WebRoot = webRoot;
        var video = await withWeb.SeedVideoAsync("AppA", withFiles: true);
        using var app = withWeb.CreateAppClient(key);
        var embed = (await (await app.PostAsJsonAsync($"/api/v1/videos/{video.PublicId}/embed", new { })).Content.ReadFromJsonAsync<EmbedResponse>())!;

        var html = await withWeb.CreateClient().GetStringAsync(PathOf(embed.EmbedUrl));

        Assert.Contains("<script type=\"module\" src=\"/assets/embed-abc123.js\"></script>", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/assets/embed-def456.css\">", html, StringComparison.Ordinal);

        // The shared player stylesheet (which sizes the video to the frame) must be linked too, before the entry's.
        var playerCss = html.IndexOf("<link rel=\"stylesheet\" href=\"/assets/player-p2.css\">", StringComparison.Ordinal);
        Assert.True(playerCss >= 0, "the shared player stylesheet is missing");
        Assert.True(playerCss < html.IndexOf("/assets/embed-def456.css", StringComparison.Ordinal));
        Assert.Contains("<link rel=\"modulepreload\" href=\"/assets/player-p1.js\">", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"modulepreload\" href=\"/assets/hls-h1.js\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", html, StringComparison.Ordinal);
        Directory.Delete(webRoot, recursive: true);
    }
}
