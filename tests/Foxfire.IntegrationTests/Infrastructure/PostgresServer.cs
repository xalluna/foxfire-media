using Npgsql;
using Testcontainers.PostgreSql;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>
/// One Postgres container per test run (started lazily, reaped by Testcontainers at process exit).
/// Every <see cref="FoxfireFactory"/> gets its own freshly-migrated database inside it.
/// </summary>
public static class PostgresServer
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(StartAsync);

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Integration tests need a running Docker daemon (Testcontainers could not start Postgres).", ex);
        }

        return container;
    }

    public static async Task<string> CreateDatabaseConnectionStringAsync()
    {
        var container = await Container.Value;
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"foxfire_test_{Guid.NewGuid():N}",
        };
        return builder.ConnectionString;
    }
}
