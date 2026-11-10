using System.Net;
using System.Net.Http.Json;
using Microsoft.Net.Http.Headers;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>
/// Drives the admin API like the SPA does: cookies are kept by the client, and the XSRF-TOKEN cookie
/// value is echoed in the X-XSRF-TOKEN header (re-fetched after sign-in, since it is bound to the user).
/// </summary>
public sealed class AdminSession(HttpClient client) : IDisposable
{
    public HttpClient Client { get; } = client;

    public static async Task<AdminSession> StartAsync(FoxfireFactory factory)
    {
        var session = new AdminSession(factory.CreateClient());
        await session.RefreshXsrfAsync();
        return session;
    }

    public static async Task<AdminSession> SignInAsync(FoxfireFactory factory, string username, string password)
    {
        var session = await StartAsync(factory);
        var response = await session.LoginAsync(username, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return session;
    }

    public async Task<HttpResponseMessage> LoginAsync(string username, string password)
    {
        var response = await Client.PostAsJsonAsync("/api/admin/auth/login", new { username, password });
        if (response.IsSuccessStatusCode)
        {
            await RefreshXsrfAsync();
        }

        return response;
    }

    public async Task RefreshXsrfAsync()
    {
        using var response = await Client.GetAsync("/api/admin/antiforgery");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = SetCookieHeaderValue.ParseList([.. response.Headers.GetValues("Set-Cookie")])
            .Single(c => c.Name == "XSRF-TOKEN");
        Client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        Client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", cookie.Value.ToString());
    }

    public void Dispose() => Client.Dispose();
}
