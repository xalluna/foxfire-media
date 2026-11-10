using System.Security.Claims;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Streaming;
using Foxfire.Server.Videos;
using Foxfire.Server.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Admin;

/// <summary>An application as the admin sees it. There is deliberately nothing about its API key here.</summary>
public sealed record AdminApplicationSummary(
    string Name,
    string DisplayName,
    string Status,
    IReadOnlyList<string> AllowedOrigins,
    string? WebhookUrl,
    int VideoCount,
    long StorageBytes,
    int PendingDeliveries,
    int DeadDeliveries,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSyncedAt,
    DateTimeOffset? MissingSince);

public sealed record AdminDeliveryRow(
    string EventId,
    string Type,
    string Status,
    int Attempt,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset NextAttemptAt);

public static class AdminApplicationApi
{
    public static RouteGroupBuilder MapAdminApplications(this RouteGroupBuilder admin)
    {
        var apps = admin.MapGroup("/applications");
        apps.MapGet("/", ListAsync);
        apps.MapGet("/{name}/webhooks", DeliveriesAsync);
        apps.MapPost("/{name}/webhooks/test", SendTestAsync);
        apps.MapPost("/{name}/webhooks/{eventId}/resend", ResendAsync);
        apps.MapPost("/{name}/revoke-tokens", RevokeTokensAsync);
        apps.MapDelete("/{name}", PurgeAsync);
        return admin;
    }

    private static Task<Application?> FindAsync(FoxfireDbContext db, string name, CancellationToken ct)
    {
        var normalized = Application.Normalize(name);
        return db.Set<Application>().SingleOrDefaultAsync(a => a.NormalizedName == normalized, ct);
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such application.");

    private static async Task<IResult> ListAsync(FoxfireDbContext db, CancellationToken ct)
    {
        var videoStats = await db.Set<Video>().AsNoTracking()
            .Where(v => v.Status != VideoStatus.Deleting)
            .GroupBy(v => v.ApplicationId)
            .Select(g => new { ApplicationId = g.Key, Count = g.Count(), Bytes = g.Sum(v => v.StorageBytes) })
            .ToDictionaryAsync(x => x.ApplicationId, ct);

        var deliveryStats = await db.Set<WebhookDelivery>().AsNoTracking()
            .Where(d => d.Status != DeliveryStatus.Succeeded)
            .GroupBy(d => new { d.ApplicationId, d.Status })
            .Select(g => new { g.Key.ApplicationId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var apps = await db.Set<Application>().AsNoTracking().OrderBy(a => a.Status).ThenBy(a => a.Name).ToListAsync(ct);
        return Results.Ok(apps.Select(a => new AdminApplicationSummary(
            a.Name,
            a.DisplayName,
            a.Status.ToString(),
            a.AllowedOrigins,
            a.WebhookUrl,
            videoStats.GetValueOrDefault(a.Id)?.Count ?? 0,
            videoStats.GetValueOrDefault(a.Id)?.Bytes ?? 0,
            deliveryStats.Where(d => d.ApplicationId == a.Id && d.Status == DeliveryStatus.Pending).Sum(d => d.Count),
            deliveryStats.Where(d => d.ApplicationId == a.Id && d.Status == DeliveryStatus.Dead).Sum(d => d.Count),
            a.CreatedAt,
            a.LastSyncedAt,
            a.MissingSince)).ToList());
    }

    private static async Task<IResult> DeliveriesAsync(string name, FoxfireDbContext db, CancellationToken ct)
    {
        var app = await FindAsync(db, name, ct);
        if (app is null)
        {
            return NotFound();
        }

        var rows = await db.Set<WebhookDelivery>().AsNoTracking()
            .Where(d => d.ApplicationId == app.Id)
            .OrderByDescending(d => d.CreatedAt)
            .Take(50)
            .Select(d => new AdminDeliveryRow(d.EventId, d.Type, d.Status.ToString(), d.Attempt, d.LastStatusCode, d.LastError, d.CreatedAt, d.DeliveredAt, d.NextAttemptAt))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> SendTestAsync(string name, ClaimsPrincipal user, FoxfireDbContext db, WebhookEvents events, TimeProvider time, CancellationToken ct)
    {
        var app = await FindAsync(db, name, ct);
        if (app is null)
        {
            return NotFound();
        }

        if (app.WebhookUrl is null || app.Status != ApplicationStatus.Active)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This application has no active webhook.");
        }

        events.RaiseRaw(app.Id, WebhookEventTypes.Test, new { message = "Test event from the FoxfireMedia admin console.", sentBy = user.Identity?.Name, sentAt = time.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    private static async Task<IResult> ResendAsync(string name, string eventId, FoxfireDbContext db, WebhookSignal signal, TimeProvider time, CancellationToken ct)
    {
        var app = await FindAsync(db, name, ct);
        var delivery = app is null ? null : await db.Set<WebhookDelivery>().SingleOrDefaultAsync(d => d.ApplicationId == app.Id && d.EventId == eventId, ct);
        if (delivery is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such delivery.");
        }

        var now = time.GetUtcNow();
        delivery.Status = DeliveryStatus.Pending;
        delivery.Attempt = 0;
        delivery.NextAttemptAt = now;
        delivery.DeliveredAt = null;
        delivery.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return Results.Accepted();
    }

    /// <summary>Invalidates every embed, playback, upload and poster token issued for the application.</summary>
    private static async Task<IResult> RevokeTokensAsync(string name, FoxfireDbContext db, VideoAccess access, CancellationToken ct)
    {
        var app = await FindAsync(db, name, ct);
        if (app is null)
        {
            return NotFound();
        }

        app.TokenEpoch++;
        await db.SaveChangesAsync(ct);
        foreach (var id in await db.Set<Video>().Where(v => v.ApplicationId == app.Id).Select(v => v.Id).ToListAsync(ct))
        {
            access.Evict(id);
        }

        return Results.NoContent();
    }

    /// <summary>Deletes an application that is no longer configured, with all of its videos and files.</summary>
    private static async Task<IResult> PurgeAsync(string name, FoxfireDbContext db, VideoDeleter deleter, CancellationToken ct)
    {
        var app = await FindAsync(db, name, ct);
        if (app is null)
        {
            return NotFound();
        }

        if (app.Status != ApplicationStatus.Missing)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Only applications removed from configuration can be purged. Remove it from appsettings.json and its ApiKeys__ variable, restart, then purge.");
        }

        foreach (var video in await db.Set<Video>().Where(v => v.ApplicationId == app.Id).ToListAsync(ct))
        {
            await deleter.DeleteAsync(video, ct);
        }

        await db.Set<WebhookDelivery>().Where(d => d.ApplicationId == app.Id).ExecuteDeleteAsync(ct);
        db.Remove(app);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
