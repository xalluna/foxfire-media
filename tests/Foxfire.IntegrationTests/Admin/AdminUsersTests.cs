using System.Net;
using System.Net.Http.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Admin;

namespace Foxfire.IntegrationTests.Admin;

public sealed class AdminUsersTests
{
    private static async Task<(FoxfireFactory Factory, string Password)> StartAsync()
    {
        var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        var password = factory.AddBootstrapAdmin("root");
        _ = factory.Server;
        return (factory, password);
    }

    private static string NewPassword() => "Pw-" + FoxfireFactory.NewKey()[..20];

    [Fact]
    public async Task Admins_can_add_another_admin_who_can_then_sign_in()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var root = await AdminSession.SignInAsync(factory, "root", password);
        var alicePassword = NewPassword();

        Assert.Equal(HttpStatusCode.Created, (await root.Client.PostAsJsonAsync("/api/admin/admins", new { username = "alice", password = alicePassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await root.Client.PostAsJsonAsync("/api/admin/admins", new { username = "ALICE", password = NewPassword() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await root.Client.PostAsJsonAsync("/api/admin/admins", new { username = "bob", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await root.Client.PostAsJsonAsync("/api/admin/admins", new { username = "no spaces", password = NewPassword() })).StatusCode);

        var list = (await root.Client.GetFromJsonAsync<List<AdminUserRow>>("/api/admin/admins"))!;
        Assert.Equal(["alice", "root"], list.Select(a => a.Username));
        Assert.True(list.Single(a => a.Username == "root").IsYou);

        using var alice = await AdminSession.SignInAsync(factory, "alice", alicePassword);
        Assert.Equal("alice", (await alice.Client.GetFromJsonAsync<AdminMeResponse>("/api/admin/auth/me"))!.Username);
        SecretScan.AssertContainsNone(await SecretScan.DumpDatabaseAsync(factory.ConnectionString), [alicePassword], "the database");
    }

    [Fact]
    public async Task You_cannot_delete_yourself_or_the_last_admin_but_can_delete_others_ending_their_sessions()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var root = await AdminSession.SignInAsync(factory, "root", password);

        Assert.Equal(HttpStatusCode.Conflict, (await root.Client.DeleteAsync("/api/admin/admins/root")).StatusCode);

        var bobPassword = NewPassword();
        await root.Client.PostAsJsonAsync("/api/admin/admins", new { username = "bob", password = bobPassword });
        using var bob = await AdminSession.SignInAsync(factory, "bob", bobPassword);
        Assert.Equal(HttpStatusCode.OK, (await bob.Client.GetAsync("/api/admin/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await root.Client.DeleteAsync("/api/admin/admins/bob")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.Client.GetAsync("/api/admin/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await root.Client.DeleteAsync("/api/admin/admins/nobody")).StatusCode);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_deleted_even_by_another_session()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var root = await AdminSession.SignInAsync(factory, "root", password);
        var carolPassword = NewPassword();
        await root.Client.PostAsJsonAsync("/api/admin/admins", new { username = "carol", password = carolPassword });
        using var carol = await AdminSession.SignInAsync(factory, "carol", carolPassword);

        Assert.Equal(HttpStatusCode.NoContent, (await carol.Client.DeleteAsync("/api/admin/admins/root")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await carol.Client.DeleteAsync("/api/admin/admins/carol")).StatusCode); // self, and last
    }

    [Fact]
    public async Task Changing_your_password_keeps_this_session_and_ends_the_others()
    {
        var (factory, password) = await StartAsync();
        await using var _ = factory;
        using var here = await AdminSession.SignInAsync(factory, "root", password);
        using var elsewhere = await AdminSession.SignInAsync(factory, "root", password);
        var replacement = NewPassword();

        var wrong = await here.Client.PostAsJsonAsync("/api/admin/auth/password", new { currentPassword = "nope-nope-nope", newPassword = replacement });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var weak = await here.Client.PostAsJsonAsync("/api/admin/auth/password", new { currentPassword = password, newPassword = "password123456" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await here.Client.PostAsJsonAsync("/api/admin/auth/password", new { currentPassword = password, newPassword = replacement })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await here.Client.GetAsync("/api/admin/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.Client.GetAsync("/api/admin/auth/me")).StatusCode);
        using var fresh = await AdminSession.StartAsync(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.LoginAsync("root", password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fresh.LoginAsync("root", replacement)).StatusCode);
    }
}
