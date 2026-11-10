using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Admin;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Admin;

public sealed class AdminAuthTests
{
    private static async Task<(FoxfireFactory Factory, string Password)> StartAsync(string? connectionString = null)
    {
        var factory = new FoxfireFactory { ConnectionString = connectionString ?? "" };
        await factory.InitializeAsync();
        var password = factory.AddBootstrapAdmin("root");
        _ = factory.Server;
        return (factory, password);
    }

    [Fact]
    public async Task The_bootstrap_admin_can_sign_in_and_out()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var session = await AdminSession.SignInAsync(factory, "ROOT", password); // usernames are case-insensitive

        var me = await session.Client.GetFromJsonAsync<AdminMeResponse>("/api/admin/auth/me");
        Assert.Equal("root", me!.Username);
        Assert.True(me.IsBootstrap);

        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync("/api/admin/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_session_cookie_is_httponly_and_strict()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var session = await AdminSession.StartAsync(factory);

        using var response = await session.LoginAsync("root", password);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AdminAuth.CookieName + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_api_requires_a_session_and_never_redirects()
    {
        var (factory, _) = await StartAsync();
        await using var __ = factory;
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task An_api_key_is_not_an_admin_credential()
    {
        var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        await using var _ = factory;
        var key = factory.AddApp("AppA");
        factory.AddBootstrapAdmin();
        using var app = factory.CreateAppClient(key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task State_changes_require_the_xsrf_header_and_a_same_origin()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var bare = factory.CreateClient();

        var noToken = await bare.PostAsJsonAsync("/api/admin/auth/login", new { username = "root", password });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        using var session = await AdminSession.StartAsync(factory);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/login") { Content = JsonContent.Create(new { username = "root", password }) };
        crossOrigin.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await session.Client.SendAsync(crossOrigin)).StatusCode);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/login") { Content = JsonContent.Create(new { username = "root", password }) };
        sameOrigin.Headers.Add("Origin", FoxfireFactory.PublicBaseUrl);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.SendAsync(sameOrigin)).StatusCode);

        // The pre-login token belongs to the anonymous user; after sign-in it no longer validates.
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsync("/api/admin/auth/logout", null)).StatusCode);
        await session.RefreshXsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync("/api/admin/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Failures_lock_the_account_with_the_same_response_as_an_unknown_user()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var session = await AdminSession.StartAsync(factory);

        var unknown = await session.LoginAsync("nobody", password);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        for (var i = 0; i < AdminAuth.MaxFailedLogins; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.LoginAsync("root", "wrong-password-" + i)).StatusCode);
        }

        var lockedOut = await session.LoginAsync("root", password);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);
        Assert.Equal(StripTrace(unknownBody), StripTrace(await lockedOut.Content.ReadAsStringAsync()));

        factory.Time.Advance(AdminAuth.LockoutDuration);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync("root", password)).StatusCode);
    }

    [Fact]
    public async Task Changing_the_security_stamp_ends_existing_sessions()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var session = await AdminSession.SignInAsync(factory, "root", password);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/admin/auth/me")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
            var admin = await db.Set<AdminUser>().SingleAsync();
            await db.Set<AdminUser>().ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid()));
            factory.Services.GetRequiredService<IMemoryCache>().Remove(AdminAuth.StampCacheKey(admin.Id));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Idle_sessions_expire()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var session = await AdminSession.SignInAsync(factory, "root", password);

        factory.Time.Advance(AdminAuth.SlidingLifetime + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_runs_once_unless_a_reset_is_requested()
    {
        var connectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();
        var (first, original) = await StartAsync(connectionString);
        await first.DisposeAsync();

        // A different bootstrap password on a later boot is ignored...
        await using (var second = new FoxfireFactory { ConnectionString = connectionString })
        {
            var ignored = second.AddBootstrapAdmin("root");
            using var session = await AdminSession.StartAsync(second);
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.LoginAsync("root", ignored)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync("root", original)).StatusCode);
        }

        // ...unless Admin__ResetBootstrapPassword=true (lockout recovery).
        await using var third = new FoxfireFactory { ConnectionString = connectionString };
        var reset = third.AddBootstrapAdmin("root");
        third.Settings["Admin:ResetBootstrapPassword"] = "true";
        using var resetSession = await AdminSession.StartAsync(third);
        Assert.Equal(HttpStatusCode.OK, (await resetSession.LoginAsync("root", reset)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await resetSession.LoginAsync("root", original)).StatusCode);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("password12345678")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa")]
    public async Task A_weak_bootstrap_password_stops_startup_without_echoing_it(string weak)
    {
        await using var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        factory.AddBootstrapAdmin("root", weak);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        while (ex is not FoxfireConfigurationException && ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        Assert.IsType<FoxfireConfigurationException>(ex);
        Assert.DoesNotContain(weak, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_are_stored_hashed()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;

        var dump = await SecretScan.DumpDatabaseAsync(factory.ConnectionString);

        Assert.DoesNotContain(password, dump, StringComparison.Ordinal);
        Assert.Contains("AQAAAA", dump, StringComparison.Ordinal); // Identity v3 hash prefix
    }

    private static string StripTrace(string body) => System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "");
}
