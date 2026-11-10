using Foxfire.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Applications;

/// <summary>Mirrors the configured applications into the database at startup and loads the key registry.</summary>
internal sealed partial class ApplicationSync(
    FoxfireDbContext db,
    ApplicationCatalog catalog,
    ApiKeyRegistry registry,
    TimeProvider time,
    ILogger<ApplicationSync> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var existing = await db.Set<Application>().ToDictionaryAsync(a => a.NormalizedName, ct);
        var configuredNames = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<ApiKeyRegistry.Entry>();

        foreach (var configured in catalog.Applications)
        {
            var normalized = Application.Normalize(configured.Name);
            configuredNames.Add(normalized);

            if (!existing.TryGetValue(normalized, out var row))
            {
                row = new Application
                {
                    Id = Guid.CreateVersion7(),
                    Name = configured.Name,
                    NormalizedName = normalized,
                    DisplayName = configured.DisplayName,
                    CreatedAt = now,
                };
                db.Add(row);
                LogAdded(logger, configured.Name);
            }
            else if (row.Status == ApplicationStatus.Missing)
            {
                LogReactivated(logger, configured.Name);
            }

            row.Name = configured.Name;
            row.DisplayName = configured.DisplayName;
            row.AllowedOrigins = [.. configured.AllowedOrigins];
            row.WebhookUrl = configured.WebhookUrl?.ToString();
            row.Status = ApplicationStatus.Active;
            row.MissingSince = null;
            row.LastSyncedAt = now;

            entries.Add(new ApiKeyRegistry.Entry(configured.ApiKey.Sha256(), row.Id, configured.Name));
            if (configured.SecondaryApiKey is { } secondary)
            {
                entries.Add(new ApiKeyRegistry.Entry(secondary.Sha256(), row.Id, configured.Name));
            }
        }

        foreach (var row in existing.Values)
        {
            if (!configuredNames.Contains(row.NormalizedName) && row.Status != ApplicationStatus.Missing)
            {
                row.Status = ApplicationStatus.Missing;
                row.MissingSince = now;
                LogMissing(logger, row.Name);
            }
        }

        await db.SaveChangesAsync(ct);
        registry.Load(entries);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Application {Name} registered")]
    private static partial void LogAdded(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Application {Name} is configured again and has been reactivated")]
    private static partial void LogReactivated(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Application {Name} is no longer configured; marked Missing (its videos are kept)")]
    private static partial void LogMissing(ILogger logger, string name);
}
