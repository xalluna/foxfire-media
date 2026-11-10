using System.Globalization;
using System.Security.Claims;
using Foxfire.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Foxfire.Server.Admin;

public sealed record AdminUserRow(string Username, bool IsBootstrap, bool IsYou, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? LockedUntil);

public sealed record CreateAdminRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public static class AdminUsersApi
{
    public static RouteGroupBuilder MapAdminUsers(this RouteGroupBuilder admin)
    {
        admin.MapGet("/admins", ListAsync);
        admin.MapPost("/admins", CreateAsync);
        admin.MapDelete("/admins/{username}", DeleteAsync);
        admin.MapPost("/auth/password", ChangePasswordAsync);
        return admin;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, FoxfireDbContext db, TimeProvider time, CancellationToken ct)
    {
        var me = user.GetAdminId();
        var now = time.GetUtcNow();
        var rows = await db.Set<AdminUser>().AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
        return Results.Ok(rows.Select(u => new AdminUserRow(u.Username, u.IsBootstrap, u.Id == me, u.CreatedAt, u.LastLoginAt, u.LockoutUntil > now ? u.LockoutUntil : null)));
    }

    private static async Task<IResult> CreateAsync(CreateAdminRequest request, FoxfireDbContext db, AdminPasswords passwords, TimeProvider time, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (AdminPasswords.ValidateUsername(request.Username) is { } usernameError)
        {
            errors["username"] = [usernameError];
        }

        if (AdminPasswords.ValidatePassword(request.Password) is { } passwordError)
        {
            errors["password"] = [passwordError];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var normalized = AdminUser.Normalize(request.Username!);
        if (await db.Set<AdminUser>().AnyAsync(u => u.NormalizedUsername == normalized, ct))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "An admin with this username already exists.");
        }

        var created = new AdminUser
        {
            Id = Guid.CreateVersion7(),
            Username = request.Username!.Trim(),
            NormalizedUsername = normalized,
            PasswordHash = "",
            SecurityStamp = Guid.NewGuid(),
            CreatedAt = time.GetUtcNow(),
        };
        created.PasswordHash = passwords.Hash(created, request.Password!);
        db.Add(created);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/admin/admins/{created.Username}", null);
    }

    private static async Task<IResult> DeleteAsync(string username, ClaimsPrincipal user, FoxfireDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        var normalized = AdminUser.Normalize(username);
        var target = await db.Set<AdminUser>().SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        if (target is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such admin.");
        }

        if (target.Id == user.GetAdminId())
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "You cannot delete your own account.");
        }

        if (await db.Set<AdminUser>().CountAsync(ct) <= 1)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The last admin cannot be deleted.");
        }

        db.Remove(target);
        await db.SaveChangesAsync(ct);
        cache.Remove(AdminAuth.StampCacheKey(target.Id)); // their open sessions end on their next request
        return Results.NoContent();
    }

    /// <summary>
    /// Changes the signed-in admin's password and rotates their security stamp: every other session of
    /// theirs ends, and this one is re-issued so it continues.
    /// </summary>
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, ClaimsPrincipal user, HttpContext context, FoxfireDbContext db, AdminPasswords passwords, IMemoryCache cache, TimeProvider time, CancellationToken ct)
    {
        var me = await db.Set<AdminUser>().SingleAsync(u => u.Id == user.GetAdminId(), ct);
        if (string.IsNullOrEmpty(request.CurrentPassword) || !passwords.Verify(me, request.CurrentPassword, out _))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["The current password is incorrect."] });
        }

        if (AdminPasswords.ValidatePassword(request.NewPassword) is { } problem)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [problem] });
        }

        me.PasswordHash = passwords.Hash(me, request.NewPassword!);
        me.SecurityStamp = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        cache.Remove(AdminAuth.StampCacheKey(me.Id));

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, me.Username),
                new Claim(AdminClaims.AdminId, me.Id.ToString()),
                new Claim(AdminClaims.Stamp, me.SecurityStamp.ToString()),
                new Claim(AdminClaims.AuthTime, time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            AdminAuth.Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        await context.SignInAsync(AdminAuth.Scheme, new ClaimsPrincipal(identity));
        return Results.NoContent();
    }
}
