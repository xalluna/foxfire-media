using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real server against an isolated Postgres database. Configure applications and settings
/// (via <see cref="AddApp"/> / <see cref="Settings"/>) before the host starts.
/// </summary>
public class FoxfireFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private int _appCount;

    public Dictionary<string, string?> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every secret value handed to the server; the leak suites assert none of them escape.</summary>
    public List<string> KnownSecrets { get; } = [];

    public CapturedLogs Logs { get; } = new();

    /// <summary>Set before initialization to reuse an existing database (e.g. to simulate a restart).</summary>
    public string ConnectionString { get; set; } = "";

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    /// <summary>Declares an application (as appsettings.json would) and its env key. Returns the key.</summary>
    public string AddApp(string name, IEnumerable<string>? origins = null, string? webhookUrl = null, string? key = null, string? secondaryKey = null)
    {
        var index = _appCount++;
        Settings[$"Applications:{index}:Name"] = name;
        var originIndex = 0;
        foreach (var origin in origins ?? [])
        {
            Settings[$"Applications:{index}:AllowedOrigins:{originIndex++}"] = origin;
        }

        if (webhookUrl is not null)
        {
            Settings[$"Applications:{index}:WebhookUrl"] = webhookUrl;
        }

        key ??= NewKey();
        Settings[$"ApiKeys:{name}"] = key;
        KnownSecrets.Add(key);
        if (secondaryKey is not null)
        {
            Settings[$"ApiKeysSecondary:{name}"] = secondaryKey;
            KnownSecrets.Add(secondaryKey);
        }

        return key;
    }

    public HttpClient CreateAppClient(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    public virtual async ValueTask InitializeAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            ConnectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // In-memory source, not UseSetting: UseSetting reaches the app as command-line arguments,
        // which the server (correctly) refuses as a source of API keys.
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>(Settings, StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Foxfire"] = ConnectionString,
                ["Webhooks:AllowHttp"] = "true",
            }));

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });
    }
}
