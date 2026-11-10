using System.Net;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests;

public sealed class HealthTests(FoxfireFactory factory) : IClassFixture<FoxfireFactory>
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_healthy_once_migrations_have_run()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/readyz", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task Model_has_no_pending_changes_and_all_migrations_are_applied()
    {
        _ = factory.Server; // ensure the host (and its startup migration) has run
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();

        Assert.False(db.Database.HasPendingModelChanges(), "Run `dotnet ef migrations add` — the model and migrations differ.");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }
}
