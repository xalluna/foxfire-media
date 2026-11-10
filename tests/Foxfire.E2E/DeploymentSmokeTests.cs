using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Foxfire.E2E;

/// <summary>
/// End-to-end checks against a running deployment (the composed Docker image in CI). Configure with:
/// <list type="bullet">
/// <item><c>FOXFIRE_E2E_BASEURL</c>: e.g. http://localhost:8080 (tests skip when unset)</item>
/// <item><c>FOXFIRE_E2E_KEY_A</c>, <c>FOXFIRE_E2E_KEY_B</c>: API keys of two configured apps; A must have an AllowedOrigin</item>
/// <item><c>FOXFIRE_E2E_CLIP</c>: path to a short H.264/AAC MP4</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
public sealed partial class DeploymentSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("FOXFIRE_E2E_BASEURL")?.TrimEnd('/');
    private static readonly string? KeyA = Environment.GetEnvironmentVariable("FOXFIRE_E2E_KEY_A");
    private static readonly string? KeyB = Environment.GetEnvironmentVariable("FOXFIRE_E2E_KEY_B");
    private static readonly string? Clip = Environment.GetEnvironmentVariable("FOXFIRE_E2E_CLIP");

    [GeneratedRegex("<script type=\"application/json\" id=\"boot\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex Boot();

    private static HttpClient Client(string? key = null)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl!), Timeout = TimeSpan.FromSeconds(30) };
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    private static HttpRequestMessage Tus(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Upload_process_embed_and_stream_a_real_video()
    {
        Assert.SkipWhen(BaseUrl is null, "FOXFIRE_E2E_BASEURL is not set");
        Assert.SkipWhen(KeyA is null || KeyB is null || Clip is null, "FOXFIRE_E2E_KEY_A/KEY_B/CLIP are not set");
        var ct = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(Clip!, ct);
        using var appA = Client(KeyA);
        using var appB = Client(KeyB);
        using var anonymous = Client();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/readyz", ct)).StatusCode);

        // 1. Mint a one-time upload ticket with the app's key.
        var mint = await appA.PostAsJsonAsync("/api/v1/uploads", new { title = "E2E clip", filename = "clip.mp4", sizeBytes = bytes.Length, externalId = "e2e-" + Guid.NewGuid().ToString("N") }, ct);
        Assert.Equal(HttpStatusCode.Created, mint.StatusCode);
        using var ticket = JsonDocument.Parse(await mint.Content.ReadAsStringAsync(ct));
        var videoId = ticket.RootElement.GetProperty("videoId").GetString()!;
        var token = ticket.RootElement.GetProperty("uploadToken").GetString()!;
        Assert.StartsWith(BaseUrl!, ticket.RootElement.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);

        // 2. Upload it the way a browser would, in two chunks, using only the upload token.
        using var create = Tus(HttpMethod.Post, "/uploads/tus", token);
        create.Headers.Add("Upload-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));
        using var created = await anonymous.SendAsync(create, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!.IsAbsoluteUri ? created.Headers.Location.AbsolutePath : created.Headers.Location.OriginalString;

        var half = bytes.Length / 2;
        foreach (var (offset, length) in new[] { (0, half), (half, bytes.Length - half) })
        {
            using var patch = Tus(HttpMethod.Patch, location, token);
            patch.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
            patch.Content = new ByteArrayContent(bytes, offset, length);
            patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
            Assert.Equal(HttpStatusCode.NoContent, (await anonymous.SendAsync(patch, ct)).StatusCode);
        }

        // The token was consumed when the upload started.
        using var reuse = Tus(HttpMethod.Post, "/uploads/tus", token);
        reuse.Headers.Add("Upload-Length", "10");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(reuse, ct)).StatusCode);

        // 3. Real ffmpeg processing.
        JsonElement video = default;
        for (var i = 0; i < 180; i++)
        {
            using var doc = JsonDocument.Parse(await appA.GetStringAsync($"/api/v1/videos/{videoId}", ct));
            video = doc.RootElement.Clone();
            if (video.GetProperty("status").GetString() is "Ready" or "Failed")
            {
                break;
            }

            await Task.Delay(1000, ct);
        }

        Assert.Equal("Ready", video.GetProperty("status").GetString());
        Assert.True(video.GetProperty("durationMs").GetInt64() > 1000);

        // 4. Isolation: app B can neither see nor embed app A's video.
        Assert.Equal(HttpStatusCode.NotFound, (await appB.GetAsync($"/api/v1/videos/{videoId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await appB.PostAsJsonAsync($"/api/v1/videos/{videoId}/embed", new { }, ct)).StatusCode);

        // 5. Embed: the page is locked to app A's origins and boots the player.
        var embedResponse = await appA.PostAsJsonAsync($"/api/v1/videos/{videoId}/embed", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, embedResponse.StatusCode);
        using var embed = JsonDocument.Parse(await embedResponse.Content.ReadAsStringAsync(ct));
        var embedPath = new Uri(embed.RootElement.GetProperty("embedUrl").GetString()!).PathAndQuery;
        using var page = await anonymous.GetAsync(embedPath, ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors http", string.Join(";", page.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Contains("/assets/embed-", html, StringComparison.Ordinal);

        using var boot = JsonDocument.Parse(Boot().Match(html).Groups[1].Value);
        var manifest = boot.RootElement.GetProperty("manifestUrl").GetString()!;

        // 6. Stream: playlist and a segment, with the no-download headers.
        using var playlist = await anonymous.GetAsync(manifest, ct);
        var playlistText = await playlist.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        Assert.Contains("#EXT-X-ENDLIST", playlistText, StringComparison.Ordinal);
        var firstSegment = playlistText.Split('\n').First(l => l.StartsWith("seg_", StringComparison.Ordinal)).Trim();
        using var segment = await anonymous.GetAsync(manifest.Replace("index.m3u8", firstSegment, StringComparison.Ordinal), ct);
        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.Equal("video/mp4", segment.Content.Headers.ContentType!.MediaType);
        Assert.Equal("same-origin", segment.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.False(segment.Content.Headers.Contains("Content-Disposition"));
        Assert.True((await segment.Content.ReadAsByteArrayAsync(ct)).Length > 1000);

        // 7. Delete: streaming stops immediately.
        Assert.Equal(HttpStatusCode.NoContent, (await appA.DeleteAsync($"/api/v1/videos/{videoId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(manifest, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await appA.GetAsync($"/api/v1/videos/{videoId}", ct)).StatusCode);
    }
}
