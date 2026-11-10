using System.Security.Claims;
using Foxfire.Server.Auth;
using Foxfire.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Applications;

public sealed record ApplicationInfoResponse(string Name, string DisplayName, IReadOnlyList<string> AllowedOrigins, string? WebhookUrl);

public static class AppApi
{
    /// <summary>The application-facing API. Every endpoint is scoped to the authenticated application.</summary>
    public static RouteGroupBuilder MapAppApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").RequireAuthorization(AuthPolicies.Application);

        api.MapGet("/me", async (ClaimsPrincipal user, FoxfireDbContext db, CancellationToken ct) =>
        {
            var appId = user.GetApplicationId();
            var app = await db.Set<Application>().AsNoTracking().SingleAsync(a => a.Id == appId, ct);
            return Results.Ok(new ApplicationInfoResponse(app.Name, app.DisplayName, app.AllowedOrigins, app.WebhookUrl));
        });

        return api;
    }
}
