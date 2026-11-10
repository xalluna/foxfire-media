using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real server against an isolated Postgres database. Subclasses (or tests) add settings
/// through <see cref="Settings"/> before the host starts.
/// </summary>
public class FoxfireFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ConnectionString { get; private set; } = "";

    public virtual async ValueTask InitializeAsync()
    {
        ConnectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Foxfire", ConnectionString);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
