using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Admin;
using Foxfire.Server.Embed;
using Foxfire.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Admin;

public sealed partial class AdminApplicationTests
{
    [Fact]
    public async Task Applications_are_listed_with_usage_and_no_key_material()
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        factory.AddApp("AppA", origins: ["https://a.example"], webhookUrl: "https://a.example/hooks", secondaryKey: FoxfireFactory.NewKey());
        factory.AddApp("AppB");
        var password = factory.AddBootstrapAdmin("root");
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        using var admin = await AdminSession.SignInAsync(factory, "root", password);

        using var response = await admin.Client.GetAsync("/api/admin/applications");
        var apps = await response.Content.ReadFromJsonAsync<List<AdminApplicationSummary>>();

        var a = apps!.Single(x => x.Name == "AppA");
        Assert.Equal("Active", a.Status);
        Assert.Equal(["https://a.example"], a.AllowedOrigins);
        Assert.Equal("https://a.example/hooks", a.WebhookUrl);
        Assert.Equal(1, a.VideoCount);
        Assert.Equal(0, apps!.Single(x => x.Name == "AppB").VideoCount);
        SecretScan.AssertContainsNone(await SecretScan.RenderAsync(response), factory.KnownSecrets, "the applications listing");
        Assert.NotEqual(Guid.Empty, video.Id);
    }

    [Fact]
    public async Task Test_events_are_delivered_logged_and_can_be_resent()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        factory.AddApp("AppA", webhookUrl: receiver.Url());
        var password = factory.AddBootstrapAdmin("root");
        using var admin = await AdminSession.SignInAsync(factory, "root", password);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.Client.PostAsync("/api/admin/applications/AppA/webhooks/test", null)).StatusCode);
        await TestData.WaitUntilAsync(() => Task.FromResult(receiver.Received.Count == 1), "test webhook");
        await TestData.WaitUntilAsync(async () =>
            (await admin.Client.GetFromJsonAsync<List<AdminDeliveryRow>>("/api/admin/applications/AppA/webhooks"))!.Single().Status == "Succeeded", "delivery logged");

        var delivery = (await admin.Client.GetFromJsonAsync<List<AdminDeliveryRow>>("/api/admin/applications/AppA/webhooks"))!.Single();
        Assert.Equal("webhook.test", delivery.Type);
        Assert.Equal(204, delivery.LastStatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.Client.PostAsync($"/api/admin/applications/AppA/webhooks/{delivery.EventId}/resend", null)).StatusCode);
        await TestData.WaitUntilAsync(() => Task.FromResult(receiver.Received.Count == 2), "resent webhook");
        Assert.Equal(delivery.EventId, receiver.Received.Last().Headers["Foxfire-Event-Id"]);
    }

    [Fact]
    public async Task Revoking_tokens_kills_outstanding_links_but_new_ones_work()
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        var key = factory.AddApp("AppA", origins: ["https://a.example"]);
        var password = factory.AddBootstrapAdmin("root");
        var video = await factory.SeedVideoAsync("AppA", withFiles: true);
        using var app = factory.CreateAppClient(key);
        using var admin = await AdminSession.SignInAsync(factory, "root", password);
        async Task<string> MintAsync() => new Uri((await (await app.PostAsJsonAsync($"/api/v1/videos/{video.PublicId}/embed", new { })).Content.ReadFromJsonAsync<EmbedResponse>())!.EmbedUrl).PathAndQuery;

        var before = await MintAsync();
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(before)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.PostAsync("/api/admin/applications/AppA/revoke-tokens", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(before)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(await MintAsync())).StatusCode);
    }

    [Fact]
    public async Task Only_missing_applications_can_be_purged_and_purging_removes_everything()
    {
        var connectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();
        Guid videoId;
        string storageRoot;
        await using (var first = new FoxfireFactory { ConnectionString = connectionString })
        {
            first.AddApp("Keep");
            first.AddApp("Gone");
            var password = first.AddBootstrapAdmin("root");
            await first.InitializeAsync();
            videoId = (await first.SeedVideoAsync("Gone", withFiles: true)).Id;
            using var admin = await AdminSession.SignInAsync(first, "root", password);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.DeleteAsync("/api/admin/applications/Gone")).StatusCode);
            storageRoot = first.StorageRoot + "-kept";
            Copy(first.StorageRoot, storageRoot);
        }

        // Restart with "Gone" removed from configuration (and a reset admin password for this boot).
        await using var third = new FoxfireFactory { ConnectionString = connectionString };
        third.Settings["Storage:Root"] = storageRoot;
        third.AddApp("Keep");
        var thirdPassword = third.AddBootstrapAdmin("root");
        third.Settings["Admin:ResetBootstrapPassword"] = "true";
        using var thirdAdmin = await AdminSession.SignInAsync(third, "root", thirdPassword);

        var listed = await thirdAdmin.Client.GetFromJsonAsync<List<AdminApplicationSummary>>("/api/admin/applications");
        Assert.Equal("Missing", listed!.Single(a => a.Name == "Gone").Status);
        Assert.Equal(HttpStatusCode.Conflict, (await thirdAdmin.Client.DeleteAsync("/api/admin/applications/Keep")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await thirdAdmin.Client.DeleteAsync("/api/admin/applications/Gone")).StatusCode);

        Assert.DoesNotContain((await thirdAdmin.Client.GetFromJsonAsync<List<AdminApplicationSummary>>("/api/admin/applications"))!, a => a.Name == "Gone");
        Assert.Null(await third.FindVideoAsync(videoId));
        Assert.Null(third.Services.GetRequiredService<IMediaStorage>().FindSource(videoId));
        Directory.Delete(storageRoot, recursive: true);
    }

    [GeneratedRegex("key|secret|password|token", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    /// <summary>No admin response type may grow a property that looks like it carries a credential.</summary>
    [Fact]
    public void Admin_response_types_have_no_credential_like_properties()
    {
        var responseTypes = typeof(AdminApplicationSummary).Assembly.GetTypes()
            .Where(t => t.Namespace is "Foxfire.Server.Admin" or "Foxfire.Server.Applications" && t.IsPublic && !t.IsAbstract)
            .Where(t => Regex.IsMatch(t.Name, "(Response|Summary|Item|Detail|Row|Option|Page|Preview)$"))
            .ToList();

        Assert.NotEmpty(responseTypes);
        foreach (var type in responseTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(SensitiveName().IsMatch(property.Name), $"{type.Name}.{property.Name} looks like a credential.");
            }
        }
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
        }
    }
}
