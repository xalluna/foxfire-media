using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Admin;
using Foxfire.Server.Media;
using Foxfire.Server.Uploads;
using Foxfire.Server.Videos;

namespace Foxfire.IntegrationTests.Admin;

public sealed class AdminUploadTests(AdminFactory factory) : IClassFixture<AdminFactory>
{
    [Fact]
    public async Task An_admin_upload_belongs_to_the_chosen_app_and_follows_the_one_time_rules()
    {
        using var admin = await factory.SignInAsync();
        var payload = new byte[4096];

        var mint = await admin.Client.PostAsJsonAsync("/api/admin/uploads", new { application = "appa", title = "Admin upload", filename = "movie.mkv", sizeBytes = payload.Length });
        Assert.Equal(HttpStatusCode.Created, mint.StatusCode);
        var ticket = (await mint.Content.ReadFromJsonAsync<UploadTicket>())!;

        // The admin UI uploads from FoxfireMedia's own origin.
        var tus = new TusClient(factory.CreateClient(), ticket.UploadToken) { Origin = FoxfireFactory.PublicBaseUrl };
        var location = await tus.CreateOrThrowAsync(payload.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await tus.PatchAsync(location, 0, payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tus.CreateAsync(payload.Length)).StatusCode);

        // It is AppA's video: AppA sees it, AppB does not.
        using var appA = factory.CreateAppClient(factory.AppAKey);
        using var appB = factory.CreateAppClient(factory.AppBKey);
        var asSeenByA = await appA.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{ticket.VideoId}");
        Assert.Equal("Admin upload", asSeenByA!.Title);
        Assert.Equal(HttpStatusCode.NotFound, (await appB.GetAsync($"/api/v1/videos/{ticket.VideoId}")).StatusCode);

        var detail = await admin.Client.GetFromJsonAsync<AdminVideoDetail>($"/api/admin/videos/{ticket.VideoId}");
        Assert.Equal("admin:root", detail!.CreatedBy);
    }

    [Fact]
    public async Task Uploading_for_an_unknown_app_is_rejected()
    {
        using var admin = await factory.SignInAsync();

        var response = await admin.Client.PostAsJsonAsync("/api/admin/uploads", new { application = "ghost", title = "x", filename = "x.mp4", sizeBytes = 10 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Application_options_list_configured_apps()
    {
        using var admin = await factory.SignInAsync();

        var options = await admin.Client.GetFromJsonAsync<List<AdminApplicationOption>>("/api/admin/application-options");

        Assert.Contains(options!, o => o.Name == "AppA" && o.Status == "Active");
        Assert.Contains(options!, o => o.Name == "AppB");
    }

    [Fact]
    public async Task Jobs_listing_shows_failures_with_their_reason()
    {
        using var admin = await factory.SignInAsync();
        for (var i = 0; i < 3; i++)
        {
            factory.Media.FailNext(new MediaProcessingException("job listing failure"));
        }

        using var app = factory.CreateAppClient(factory.AppAKey);
        var ticket = await TusClient.MintAsync(app, title: "Will fail", sizeBytes: 8);
        var tus = new TusClient(factory.CreateClient(), ticket.UploadToken);
        await tus.PatchAsync(await tus.CreateOrThrowAsync(8), 0, new byte[8]);

        await TestData.WaitUntilAsync(async () =>
            (await admin.Client.GetFromJsonAsync<List<AdminJobRow>>("/api/admin/jobs"))!.Any(j => j.VideoId == ticket.VideoId && j.Status == "Failed"), "failed job");

        var jobs = await admin.Client.GetFromJsonAsync<List<AdminJobRow>>("/api/admin/jobs");
        var job = jobs!.Single(j => j.VideoId == ticket.VideoId);
        Assert.Equal("Will fail", job.VideoTitle);
        Assert.Equal("AppA", job.Application);
        Assert.Equal("job listing failure", job.LastError);
        Assert.Equal("upload", job.Reason);
    }
}
