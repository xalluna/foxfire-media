using System.Net.Http.Headers;
using Foxfire.IntegrationTests.Infrastructure;

namespace Foxfire.IntegrationTests.Security;

/// <summary>
/// The "API keys are never visible" guarantee: after exercising the server, no configured key (nor a
/// presented wrong key) appears in any log line, database row, or response.
/// Later features extend <see cref="Scenario"/> with their own endpoints.
/// </summary>
public sealed class SecretLeakTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Secrets_never_reach_logs_database_or_responses()
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        var keyA = factory.AddApp("AppA", origins: ["https://a.example"], webhookUrl: "https://a.example/hooks", secondaryKey: FoxfireFactory.NewKey());
        factory.AddApp("AppB");
        var adminPassword = factory.AddBootstrapAdmin("root");
        var wrongKey = FoxfireFactory.NewKey();
        var secrets = factory.KnownSecrets.Append(wrongKey).ToList();

        var responses = await Scenario(factory, keyA, wrongKey);

        // Admin sign-in (right and wrong passwords) and admin reads.
        using (var admin = await AdminSession.StartAsync(factory))
        {
            using var wrong = await admin.LoginAsync("root", "Wrong-" + adminPassword);
            responses += await SecretScan.RenderAsync(wrong);
            using var right = await admin.LoginAsync("root", adminPassword);
            responses += await SecretScan.RenderAsync(right);
            using var me = await admin.Client.GetAsync("/api/admin/auth/me");
            responses += await SecretScan.RenderAsync(me);
        }

        SecretScan.AssertContainsNone(responses, secrets, "HTTP responses");
        SecretScan.AssertContainsNone(factory.Logs.All, secrets, "logs");
        SecretScan.AssertContainsNone(await SecretScan.DumpDatabaseAsync(factory.ConnectionString), secrets, "the database");
        Assert.NotEmpty(factory.Logs.Lines); // the capture really ran
    }

    private static async Task<string> Scenario(FoxfireFactory factory, string validKey, string wrongKey)
    {
        var rendered = new List<string>();
        using var valid = factory.CreateAppClient(validKey);
        using var wrong = factory.CreateAppClient(wrongKey);
        using var anonymous = factory.CreateClient();

        async Task Send(HttpClient client, string url)
        {
            using var response = await client.GetAsync(url, Ct);
            rendered.Add(await SecretScan.RenderAsync(response));
        }

        await Send(valid, "/api/v1/me");
        await Send(wrong, "/api/v1/me");
        await Send(anonymous, "/api/v1/me");
        await Send(valid, $"/api/v1/me?api_key={Uri.EscapeDataString(validKey)}");
        await Send(anonymous, $"/api/v1/me?key={Uri.EscapeDataString(wrongKey)}");
        await Send(anonymous, "/healthz");
        await Send(anonymous, "/readyz");
        await Send(anonymous, "/does-not-exist");

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);
        await Send(bearer, "/api/v1/me");

        // Upload flow: the upload token is handed to the app by design, but must never be logged.
        var ticket = await TusClient.MintAsync(valid, sizeBytes: 10);
        var tus = new TusClient(factory.CreateClient(), ticket.UploadToken);
        var location = await tus.CreateOrThrowAsync(10);
        using (var patch = await tus.PatchAsync(location, 0, new byte[10]))
        {
            rendered.Add(await SecretScan.RenderAsync(patch));
        }

        SecretScan.AssertContainsNone(factory.Logs.All, [ticket.UploadToken], "logs (upload token)");

        await Send(valid, "/api/v1/videos");

        return string.Join('\n', rendered);
    }
}
