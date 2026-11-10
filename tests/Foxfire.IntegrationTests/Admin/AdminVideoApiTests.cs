using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Admin;
using Foxfire.Server.Videos;

namespace Foxfire.IntegrationTests.Admin;

public sealed class AdminFactory : FoxfireFactory
{
    public AdminFactory()
    {
        AppAKey = AddApp("AppA", origins: ["https://a.example"]);
        AppBKey = AddApp("AppB");
        Password = AddBootstrapAdmin("root");
    }

    public string AppAKey { get; }
    public string AppBKey { get; }
    public string Password { get; }

    public Task<AdminSession> SignInAsync() => AdminSession.SignInAsync(this, "root", Password);
}

public sealed class AdminVideoApiTests(AdminFactory factory) : IClassFixture<AdminFactory>
{
    [Fact]
    public async Task Admins_see_every_applications_videos_and_can_filter_and_search()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var a = await factory.SeedVideoAsync("AppA", title: $"Raid night {unique}", withFiles: true);
        var b = await factory.SeedVideoAsync("AppB", title: $"Speedrun {unique}", status: VideoStatus.Failed);
        using var admin = await factory.SignInAsync();

        var searched = await admin.Client.GetFromJsonAsync<AdminVideoPage>($"/api/admin/videos?q={unique}");
        Assert.Equal(new[] { b.PublicId, a.PublicId }.Order(), searched!.Items.Select(i => i.Id).Order());

        var onlyB = await admin.Client.GetFromJsonAsync<AdminVideoPage>($"/api/admin/videos?q={unique}&app=appb");
        var item = Assert.Single(onlyB!.Items);
        Assert.Equal("AppB", item.Application);
        Assert.Equal("Failed", item.Status);
        Assert.Null(item.ThumbnailUrl);

        var ready = await admin.Client.GetFromJsonAsync<AdminVideoPage>($"/api/admin/videos?q={unique}&status=ready");
        var readyItem = Assert.Single(ready!.Items);
        Assert.NotNull(readyItem.ThumbnailUrl);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync(readyItem.ThumbnailUrl)).StatusCode);

        var wildcard = await admin.Client.GetFromJsonAsync<AdminVideoPage>("/api/admin/videos?q=%25"); // a literal '%' matches nothing special
        Assert.DoesNotContain(wildcard!.Items, i => i.Id == a.PublicId);
    }

    [Fact]
    public async Task Detail_edit_and_delete()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true, externalId: "ext-" + Guid.NewGuid().ToString("N")[..6]);
        using var admin = await factory.SignInAsync();

        var detail = await admin.Client.GetFromJsonAsync<AdminVideoDetail>($"/api/admin/videos/{video.PublicId}");
        Assert.Equal("AppA", detail!.Application);
        Assert.True(detail.HasSource);
        Assert.True(detail.OutputBytes > 0);
        Assert.Equal(video.ExternalId, detail.ExternalId);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.PatchAsJsonAsync($"/api/admin/videos/{video.PublicId}", new { title = "Renamed by admin" })).StatusCode);
        using (var app = factory.CreateAppClient(factory.AppAKey))
        {
            Assert.Equal("Renamed by admin", (await app.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{video.PublicId}"))!.Title);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"/api/admin/videos/{video.PublicId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync($"/api/admin/videos/{video.PublicId}")).StatusCode);
        Assert.Null(await factory.FindVideoAsync(video.Id));
    }

    [Fact]
    public async Task Reprocess_encodes_again_from_the_kept_original()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        using var admin = await factory.SignInAsync();
        var callsBefore = factory.Media.Calls;

        var response = await admin.Client.PostAsync($"/api/admin/videos/{video.PublicId}/reprocess", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await factory.WaitForStatusAsync(video.Id, VideoStatus.Ready);
        await TestData.WaitUntilAsync(async () =>
            (await admin.Client.GetFromJsonAsync<AdminVideoDetail>($"/api/admin/videos/{video.PublicId}"))!.Jobs.FirstOrDefault()?.Status == "Succeeded", "job success");
        var detail = await admin.Client.GetFromJsonAsync<AdminVideoDetail>($"/api/admin/videos/{video.PublicId}");
        Assert.Equal("admin:root", detail!.Jobs[0].Reason);
        Assert.True(factory.Media.Calls > callsBefore);
    }

    [Fact]
    public async Task Reprocess_is_refused_without_a_kept_original_or_while_not_settled()
    {
        var noSource = await factory.SeedVideoAsync("AppA");
        var uploading = await factory.SeedVideoAsync("AppA", status: VideoStatus.Uploading, withSource: true);
        using var admin = await factory.SignInAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.PostAsync($"/api/admin/videos/{noSource.PublicId}/reprocess", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.PostAsync($"/api/admin/videos/{uploading.PublicId}/reprocess", null)).StatusCode);
    }

    [Fact]
    public async Task Preview_issues_a_same_origin_playback_session()
    {
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        using var admin = await factory.SignInAsync();

        var preview = await (await admin.Client.PostAsync($"/api/admin/videos/{video.PublicId}/preview", null)).Content.ReadFromJsonAsync<AdminPreview>();

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(preview!.ManifestUrl)).StatusCode);
    }

    [Fact]
    public async Task The_admin_video_api_requires_an_admin_session_and_csrf()
    {
        var video = await factory.SeedVideoAsync("AppA");
        using var anonymous = factory.CreateClient();
        using var app = factory.CreateAppClient(factory.AppAKey);
        using var admin = await factory.SignInAsync();
        admin.Client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/videos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/admin/videos")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.DeleteAsync($"/api/admin/videos/{video.PublicId}")).StatusCode);
        Assert.NotNull(await factory.FindVideoAsync(video.Id));
    }
}
