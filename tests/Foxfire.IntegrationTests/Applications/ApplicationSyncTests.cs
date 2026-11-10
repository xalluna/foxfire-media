using System.Net;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Applications;

public sealed class ApplicationSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Application>> LoadAppsAsync(FoxfireFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<Application>()
            .AsNoTracking().OrderBy(a => a.Name).ToListAsync(Ct);
    }

    [Fact]
    public async Task Apps_are_registered_marked_missing_and_reactivated_across_restarts()
    {
        var connectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();

        // Boot 1: AppA and AppB configured.
        await using (var boot1 = new FoxfireFactory { ConnectionString = connectionString })
        {
            boot1.AddApp("AppA", origins: ["https://a.example"]);
            boot1.AddApp("AppB");
            var apps = await LoadAppsAsync(boot1);

            Assert.Equal(["AppA", "AppB"], apps.Select(a => a.Name));
            Assert.All(apps, a => Assert.Equal(ApplicationStatus.Active, a.Status));
            Assert.Equal(["https://a.example"], apps[0].AllowedOrigins);
        }

        // Boot 2: AppB removed from config, AppA's origins changed. AppB's old key no longer works.
        string appBKeyAfterReAdd;
        await using (var boot2 = new FoxfireFactory { ConnectionString = connectionString })
        {
            boot2.AddApp("AppA", origins: ["https://a2.example"]);
            var apps = await LoadAppsAsync(boot2);

            Assert.Equal(ApplicationStatus.Active, apps[0].Status);
            Assert.Equal(["https://a2.example"], apps[0].AllowedOrigins);
            Assert.Equal(ApplicationStatus.Missing, apps[1].Status);
            Assert.NotNull(apps[1].MissingSince);
        }

        // Boot 3: AppB is back with a brand-new key; same row (same id) is reactivated.
        await using (var boot3 = new FoxfireFactory { ConnectionString = connectionString })
        {
            boot3.AddApp("AppA");
            appBKeyAfterReAdd = boot3.AddApp("appb"); // names match case-insensitively
            var apps = await LoadAppsAsync(boot3);

            Assert.Equal(2, apps.Count);
            Assert.All(apps, a => Assert.Equal(ApplicationStatus.Active, a.Status));
            Assert.Null(apps.Single(a => a.NormalizedName == "APPB").MissingSince);

            using var client = boot3.CreateAppClient(appBKeyAfterReAdd);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Startup_fails_when_a_declared_app_has_no_key_and_the_error_holds_no_secret()
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        var otherKey = factory.AddApp("AppA");
        factory.Settings["Applications:1:Name"] = "AppB"; // declared, but no ApiKeys__AppB

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);

        var config = Assert.IsType<FoxfireConfigurationException>(Unwrap(ex));
        Assert.Contains(config.Problems, p => p.Contains("ApiKeys__AppB", StringComparison.Ordinal));
        Assert.DoesNotContain(otherKey, config.Message, StringComparison.Ordinal);
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is not FoxfireConfigurationException && ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }
}
