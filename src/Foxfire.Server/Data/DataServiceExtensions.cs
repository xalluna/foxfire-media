using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Data;

public static class DataServiceExtensions
{
    public const string ConnectionStringName = "Foxfire";

    public static IServiceCollection AddFoxfireData(this IServiceCollection services)
    {
        // Configuration is read lazily (at resolve time) so test hosts can override it.
        services.AddDbContext<FoxfireDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
            }

            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
        });

        services.AddDataProtection()
            .SetApplicationName("foxfire-media")
            .PersistKeysToDbContext<FoxfireDbContext>();

        services.AddHealthChecks()
            .AddDbContextCheck<FoxfireDbContext>("database", tags: ["ready"]);

        return services;
    }

    public static async Task MigrateFoxfireDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        await db.Database.MigrateAsync(ct);
    }
}
