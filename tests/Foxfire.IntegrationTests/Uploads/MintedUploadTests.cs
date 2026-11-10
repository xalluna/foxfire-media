using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.IntegrationTests.Videos;
using Foxfire.Server.Storage;
using Foxfire.Server.Uploads;
using Foxfire.Server.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Uploads;

public sealed class MintedUploadTests(TwoAppFactory factory) : IClassFixture<TwoAppFactory>
{
    private static readonly byte[] Payload = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i % 251))];

    private HttpClient AppA() => factory.CreateAppClient(factory.AppAKey);

    private TusClient Tus(string token) => new(factory.CreateClient(), token);

    private async Task<Video> VideoAsync(string publicId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Server.Data.FoxfireDbContext>();
        return await db.Set<Video>().AsNoTracking().SingleAsync(v => v.PublicId == publicId);
    }

    private Task SweepAsync() => factory.Services.GetRequiredService<UploadSweeper>().SweepAsync(CancellationToken.None);

    [Fact]
    public async Task Mint_returns_a_one_time_ticket_and_creates_an_awaiting_video()
    {
        using var app = AppA();

        var ticket = await TusClient.MintAsync(app, title: "Movie night", sizeBytes: 3000);

        Assert.Equal($"{FoxfireFactory.PublicBaseUrl}/uploads/tus", ticket.UploadUrl);
        Assert.Equal(3000, ticket.MaxBytes);
        Assert.Equal(factory.Time.GetUtcNow().AddMinutes(15), ticket.ExpiresAt);
        var video = await app.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{ticket.VideoId}");
        Assert.Equal("AwaitingUpload", video!.Status);
        Assert.Equal("Movie night", video.Title);
    }

    [Fact]
    public async Task Chunked_resumable_upload_completes_and_queues_the_video_with_the_original_kept()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app, sizeBytes: Payload.Length, filename: "Gameplay VOD.MKV");
        var tus = Tus(ticket.UploadToken);

        var location = await tus.CreateOrThrowAsync(Payload.Length);
        Assert.Equal("Uploading", (await app.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{ticket.VideoId}"))!.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await tus.PatchAsync(location, 0, Payload[..1000])).StatusCode);

        // Simulate a dropped connection: the client asks where to resume.
        using var head = await tus.HeadAsync(location);
        Assert.Equal(1000, TusClient.Offset(head));

        Assert.Equal(HttpStatusCode.NoContent, (await tus.PatchAsync(location, 1000, Payload[1000..2000])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tus.PatchAsync(location, 2000, Payload[2000..])).StatusCode);

        var video = await VideoAsync(ticket.VideoId);
        Assert.Equal(VideoStatus.Queued, video.Status);
        Assert.Equal(Payload.Length, video.SourceBytes);
        Assert.Null(video.TusFileId);

        var source = factory.Services.GetRequiredService<IMediaStorage>().FindSource(video.Id);
        Assert.NotNull(source);
        Assert.EndsWith("source.mkv", source, StringComparison.Ordinal);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.EnumerateFiles(factory.Services.GetRequiredService<IMediaStorage>().UploadsDirectory, location.Split('/')[^1] + "*"));
    }

    [Fact]
    public async Task A_token_cannot_start_a_second_upload()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app);
        var tus = Tus(ticket.UploadToken);

        await tus.CreateOrThrowAsync(Payload.Length);
        using var second = await tus.CreateAsync(Payload.Length);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Concurrent_creations_with_one_token_yield_exactly_one_upload()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Tus(ticket.UploadToken).CreateAsync(Payload.Length)));

        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, attempts.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task A_token_must_be_used_within_its_start_window()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app);

        factory.Time.Advance(TimeSpan.FromMinutes(15));
        using var late = await Tus(ticket.UploadToken).CreateAsync(Payload.Length);

        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);

        await SweepAsync();
        Assert.Equal(VideoStatus.UploadExpired, (await VideoAsync(ticket.VideoId)).Status);
    }

    [Fact]
    public async Task Renewing_issues_a_new_token_and_invalidates_the_old_one()
    {
        using var app = AppA();
        var first = await TusClient.MintAsync(app);

        var renew = await app.PostAsJsonAsync($"/api/v1/videos/{first.VideoId}/upload-token", new { });
        var second = await renew.Content.ReadFromJsonAsync<UploadTicket>();

        Assert.Equal(HttpStatusCode.OK, renew.StatusCode);
        Assert.Equal(first.VideoId, second!.VideoId);
        Assert.NotEqual(first.UploadToken, second.UploadToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Tus(first.UploadToken).CreateAsync(Payload.Length)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Tus(second.UploadToken).CreateAsync(Payload.Length)).StatusCode);
    }

    [Fact]
    public async Task A_stalled_upload_fails_its_partial_bytes_are_deleted_and_a_new_token_is_required()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app, sizeBytes: Payload.Length);
        var tus = Tus(ticket.UploadToken);
        var location = await tus.CreateOrThrowAsync(Payload.Length);
        await tus.PatchAsync(location, 0, Payload[..500]);
        var fileId = location.Split('/')[^1];
        var uploads = factory.Services.GetRequiredService<IMediaStorage>().UploadsDirectory;
        Assert.NotEmpty(Directory.EnumerateFiles(uploads, fileId + "*"));

        factory.Time.Advance(TimeSpan.FromMinutes(11));
        await SweepAsync();

        var video = await VideoAsync(ticket.VideoId);
        Assert.Equal(VideoStatus.UploadFailed, video.Status);
        Assert.Empty(Directory.EnumerateFiles(uploads, fileId + "*"));
        Assert.Equal(HttpStatusCode.NotFound, (await tus.HeadAsync(location)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tus.PatchAsync(location, 500, Payload[500..])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tus.CreateAsync(Payload.Length)).StatusCode);
        var failed = await app.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{ticket.VideoId}");
        Assert.Contains("stalled", failed!.Error, StringComparison.Ordinal);

        var renewed = await (await app.PostAsJsonAsync($"/api/v1/videos/{ticket.VideoId}/upload-token", new { })).Content.ReadFromJsonAsync<UploadTicket>();
        var retry = Tus(renewed!.UploadToken);
        var retryLocation = await retry.CreateOrThrowAsync(Payload.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await retry.PatchAsync(retryLocation, 0, Payload)).StatusCode);
        Assert.Equal(VideoStatus.Queued, (await VideoAsync(ticket.VideoId)).Status);
    }

    [Fact]
    public async Task A_client_abort_fails_the_upload()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app);
        var tus = Tus(ticket.UploadToken);
        var location = await tus.CreateOrThrowAsync(Payload.Length);

        Assert.Equal(HttpStatusCode.NoContent, (await tus.DeleteAsync(location)).StatusCode);

        Assert.Equal(VideoStatus.UploadFailed, (await VideoAsync(ticket.VideoId)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tus.CreateAsync(Payload.Length)).StatusCode);
    }

    [Fact]
    public async Task A_token_only_reaches_the_file_it_created()
    {
        using var app = AppA();
        var one = await TusClient.MintAsync(app);
        var two = await TusClient.MintAsync(app);
        var locationOne = await Tus(one.UploadToken).CreateOrThrowAsync(Payload.Length);
        await Tus(two.UploadToken).CreateOrThrowAsync(Payload.Length);

        var intruder = Tus(two.UploadToken);

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.HeadAsync(locationOne)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PatchAsync(locationOne, 0, Payload[..10])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.DeleteAsync(locationOne)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Tus("v1.garbage.token").HeadAsync(locationOne)).StatusCode);
        Assert.Equal(VideoStatus.Uploading, (await VideoAsync(one.VideoId)).Status);
    }

    [Fact]
    public async Task Creating_more_than_the_declared_size_is_refused()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app, sizeBytes: 1000);

        using var response = await Tus(ticket.UploadToken).CreateAsync(1001);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(VideoStatus.AwaitingUpload, (await VideoAsync(ticket.VideoId)).Status); // not consumed
    }

    [Fact]
    public async Task Low_disk_space_refuses_minting_and_creation()
    {
        await using var isolated = new FoxfireFactory();
        await isolated.InitializeAsync();
        var key = isolated.AddApp("AppA");
        using var app = isolated.CreateAppClient(key);
        var ticket = await TusClient.MintAsync(app, sizeBytes: 3000);

        isolated.Disk.FreeBytes = 1024;

        var mint = await app.PostAsJsonAsync("/api/v1/uploads", new { title = "x", filename = "x.mp4", sizeBytes = 3000 });
        Assert.Equal(HttpStatusCode.InsufficientStorage, mint.StatusCode);
        using var create = await new TusClient(isolated.CreateClient(), ticket.UploadToken).CreateAsync(3000);
        Assert.Equal(HttpStatusCode.InsufficientStorage, create.StatusCode);
    }

    [Fact]
    public async Task Renewal_is_scoped_to_the_owner_and_refused_once_an_upload_is_under_way()
    {
        using var app = AppA();
        using var appB = factory.CreateAppClient(factory.AppBKey);
        var ticket = await TusClient.MintAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await appB.PostAsJsonAsync($"/api/v1/videos/{ticket.VideoId}/upload-token", new { })).StatusCode);

        await Tus(ticket.UploadToken).CreateOrThrowAsync(Payload.Length);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsJsonAsync($"/api/v1/videos/{ticket.VideoId}/upload-token", new { })).StatusCode);
    }

    [Theory]
    [InlineData("""{"filename":"a.mp4","sizeBytes":10}""")]
    [InlineData("""{"title":"t","sizeBytes":10}""")]
    [InlineData("""{"title":"t","filename":"a.mp4","sizeBytes":0}""")]
    [InlineData("""{"title":"t","filename":"a.mp4","sizeBytes":999999999999999}""")]
    public async Task Invalid_mint_requests_are_rejected(string json)
    {
        using var app = AppA();

        var response = await app.PostAsync("/api/v1/uploads", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_video_mid_upload_removes_the_partial_file()
    {
        using var app = AppA();
        var ticket = await TusClient.MintAsync(app);
        var tus = Tus(ticket.UploadToken);
        var location = await tus.CreateOrThrowAsync(Payload.Length);
        await tus.PatchAsync(location, 0, Payload[..100]);

        Assert.Equal(HttpStatusCode.NoContent, (await app.DeleteAsync($"/api/v1/videos/{ticket.VideoId}")).StatusCode);

        var uploads = factory.Services.GetRequiredService<IMediaStorage>().UploadsDirectory;
        Assert.Empty(Directory.EnumerateFiles(uploads, location.Split('/')[^1] + "*"));
        Assert.Equal(HttpStatusCode.NotFound, (await tus.HeadAsync(location)).StatusCode);
    }
}
