using System.Globalization;
using System.Security.Claims;
using Foxfire.Server.Applications;
using Foxfire.Server.Auth;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Foxfire.Server.Admin;

public static class AdminClaims
{
    public const string AdminId = "foxfire:admin_id";
    public const string Stamp = "foxfire:stamp";
    public const string AuthTime = "foxfire:auth_time";

    public static Guid GetAdminId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(AdminId) ?? throw new InvalidOperationException("Not an admin principal."));

    public static string AuditName(this ClaimsPrincipal user) => "admin:" + (user.Identity?.Name ?? "unknown");
}

public sealed record LoginRequest(string? Username, string? Password);

public sealed record AdminMeResponse(string Username, bool IsBootstrap);

/// <summary>
/// Admin sign-in: cookie auth (HttpOnly, SameSite=Strict, Secure), 8 h sliding / 7 day absolute,
/// lockout after repeated failures, and a security stamp checked every minute so a password change
/// or account deletion ends existing sessions. Every state-changing admin request must carry a valid
/// antiforgery header and, when present, a same-origin Origin.
/// </summary>
public static partial class AdminAuth
{
    public const string Scheme = "AdminCookie";
    public const string Policy = "Admin";
    public const string CookieName = "foxfire_admin";
    public const string XsrfCookieName = "XSRF-TOKEN";
    public const string XsrfHeaderName = "X-XSRF-TOKEN";
    public const string BootstrapUsernamePath = "Admin:BootstrapUsername";
    public const string BootstrapPasswordPath = "Admin:BootstrapPassword";
    public const string ResetBootstrapPasswordPath = "Admin:ResetBootstrapPassword";

    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan StampCheckCache = TimeSpan.FromSeconds(60);

    public static IServiceCollection AddFoxfireAdminAuth(this IServiceCollection services)
    {
        services.AddSingleton<AdminPasswords>();

        // The admin cookie is the default identity for requests without an explicit policy, so
        // antiforgery tokens issued after sign-in are bound to the signed-in admin. App API
        // endpoints name the ApiKey scheme explicitly and are unaffected.
        services.AddAuthentication(options => options.DefaultAuthenticateScheme = Scheme)
            .AddCookie(Scheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                // Secure whenever the request is https; with an https PublicBaseUrl every request is (see UsePublicScheme).
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = SlidingLifetime;
                options.SlidingExpiration = true;
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
                    OnValidatePrincipal = ValidatePrincipalAsync,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(AdminClaims.AdminId));

        services.AddAntiforgery(options =>
        {
            options.HeaderName = XsrfHeaderName;
            options.Cookie.Name = "foxfire_af";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        return services;
    }

    /// <summary>The admin API group: cookie-authenticated, CSRF-protected on every state change.</summary>
    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin").AddEndpointFilter(CsrfFilterAsync);

        admin.MapGet("/antiforgery", IssueAntiforgery);
        admin.MapPost("/auth/login", LoginAsync);
        admin.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(Scheme);
            return Results.NoContent();
        });

        var authenticated = admin.MapGroup("").RequireAuthorization(Policy);
        authenticated.MapGet("/auth/me", async (ClaimsPrincipal user, FoxfireDbContext db, CancellationToken ct) =>
        {
            var id = user.GetAdminId();
            var me = await db.Set<AdminUser>().AsNoTracking().SingleAsync(u => u.Id == id, ct);
            return Results.Ok(new AdminMeResponse(me.Username, me.IsBootstrap));
        });

        return authenticated;
    }

    /// <summary>Issues the double-submit token: a JS-readable XSRF-TOKEN cookie the SPA echoes in X-XSRF-TOKEN.</summary>
    private static IResult IssueAntiforgery(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(XsrfCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
        });
        context.Response.Headers.CacheControl = "no-store";
        return Results.NoContent();
    }

    private static async ValueTask<object?> CsrfFilterAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
        {
            return await next(invocation);
        }

        var urls = context.RequestServices.GetRequiredService<PublicUrls>();
        if (context.Request.Headers.Origin is { Count: > 0 } origin &&
            !string.Equals(OriginRules.Normalize(origin[0]), urls.Origin.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Cross-origin admin requests are not allowed.");
        }

        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing or invalid X-XSRF-TOKEN header. Fetch /api/admin/antiforgery first.");
        }

        return await next(invocation);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        FoxfireDbContext db,
        AdminPasswords passwords,
        FailedAuthLimiter limiter,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("Foxfire.Admin.Auth");
        if (limiter.IsBlocked(context))
        {
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed sign-in attempts.");
        }

        var invalid = Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password.");
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password) || request.Password.Length > AdminPasswords.MaxLength)
        {
            return invalid;
        }

        var now = time.GetUtcNow();
        var normalized = AdminUser.Normalize(request.Username);
        var user = await db.Set<AdminUser>().SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, context.RequestAborted);

        var locked = user?.LockoutUntil > now;
        var valid = passwords.Verify(user, request.Password, out var rehash) && !locked;
        if (!valid)
        {
            limiter.RecordFailure(context);
            if (user is not null && !locked)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= MaxFailedLogins)
                {
                    user.LockoutUntil = now + LockoutDuration;
                    user.FailedLoginCount = 0;
                    LogLockedOut(logger, user.Username);
                }

                await db.SaveChangesAsync(context.RequestAborted);
            }

            return invalid; // identical whether the user is unknown, locked out, or the password is wrong
        }

        user!.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        if (rehash)
        {
            user.PasswordHash = passwords.Hash(user, request.Password);
        }

        await db.SaveChangesAsync(context.RequestAborted);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(AdminClaims.AdminId, user.Id.ToString()),
                new Claim(AdminClaims.Stamp, user.SecurityStamp.ToString()),
                new Claim(AdminClaims.AuthTime, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        await context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
        LogSignedIn(logger, user.Username);
        return Results.Ok(new AdminMeResponse(user.Username, user.IsBootstrap));
    }

    /// <summary>Rejects sessions past the absolute lifetime, or whose user was deleted or changed password.</summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var services = context.HttpContext.RequestServices;
        var time = services.GetRequiredService<TimeProvider>();

        if (principal?.FindFirstValue(AdminClaims.AdminId) is not { } idText || !Guid.TryParse(idText, out var id) ||
            principal.FindFirstValue(AdminClaims.Stamp) is not { } stamp ||
            !long.TryParse(principal.FindFirstValue(AdminClaims.AuthTime), out var authTime) ||
            time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(authTime) > AbsoluteLifetime)
        {
            await Reject(context);
            return;
        }

        var cache = services.GetRequiredService<IMemoryCache>();
        var current = await cache.GetOrCreateAsync(StampCacheKey(id), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = StampCheckCache;
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<AdminUser>()
                .Where(u => u.Id == id).Select(u => (Guid?)u.SecurityStamp).SingleOrDefaultAsync();
        });

        if (current?.ToString() != stamp)
        {
            await Reject(context);
        }

        static async Task Reject(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
        }
    }

    public static string StampCacheKey(Guid adminId) => "admin-stamp:" + adminId.ToString("N");

    /// <summary>
    /// Creates the first admin from Admin__BootstrapUsername / Admin__BootstrapPassword when there are
    /// none (or resets that admin's password when Admin__ResetBootstrapPassword=true). The password is
    /// treated like an API key: environment only, never logged.
    /// </summary>
    public static async Task BootstrapAdminAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var problems = SecretSourceGuard.FindViolations(configuration, [BootstrapPasswordPath]).ToList();
        if (problems.Count > 0)
        {
            throw new FoxfireConfigurationException(problems);
        }

        var username = configuration[BootstrapUsernamePath];
        var password = configuration[BootstrapPasswordPath] is { Length: > 0 } raw ? new Secret(raw) : null;
        var reset = configuration.GetValue<bool>(ResetBootstrapPasswordPath);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<AdminPasswords>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Foxfire.Admin.Bootstrap");
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var anyAdmin = await db.Set<AdminUser>().AnyAsync(ct);
        if (username is null || password is null)
        {
            if (!anyAdmin)
            {
                LogNoAdmins(logger);
            }

            return;
        }

        if (AdminPasswords.ValidateUsername(username) is { } usernameProblem)
        {
            throw new FoxfireConfigurationException([$"Admin__BootstrapUsername: {usernameProblem}"]);
        }

        if (AdminPasswords.ValidatePassword(password.Reveal()) is { } passwordProblem)
        {
            throw new FoxfireConfigurationException([$"Admin__BootstrapPassword: {passwordProblem}"]);
        }

        var normalized = AdminUser.Normalize(username);
        var existing = await db.Set<AdminUser>().SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        if (anyAdmin && !reset)
        {
            LogBootstrapIgnored(logger);
            return;
        }

        if (existing is null)
        {
            existing = new AdminUser
            {
                Id = Guid.CreateVersion7(),
                Username = username,
                NormalizedUsername = normalized,
                PasswordHash = "",
                IsBootstrap = true,
                CreatedAt = time.GetUtcNow(),
            };
            db.Add(existing);
        }

        existing.PasswordHash = passwords.Hash(existing, password.Reveal());
        existing.SecurityStamp = Guid.NewGuid();
        existing.FailedLoginCount = 0;
        existing.LockoutUntil = null;
        await db.SaveChangesAsync(ct);
        LogBootstrapped(logger, existing.Username, reset);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {Username} signed in")]
    private static partial void LogSignedIn(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin {Username} is locked out after repeated failed sign-ins")]
    private static partial void LogLockedOut(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No admin accounts exist. Set Admin__BootstrapUsername and Admin__BootstrapPassword to create one.")]
    private static partial void LogNoAdmins(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap admin settings ignored: admin accounts already exist (set Admin__ResetBootstrapPassword=true to reset)")]
    private static partial void LogBootstrapIgnored(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap admin {Username} provisioned (reset: {Reset})")]
    private static partial void LogBootstrapped(ILogger logger, string username, bool reset);
}
