using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foxfire.IntegrationTests.Infrastructure;

public sealed record ReceivedWebhook(string Path, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A real HTTP endpoint on 127.0.0.1 that records webhook calls. Responses can be scripted per call
/// (e.g. fail a few times, or redirect) to exercise the dispatcher's retry and redirect rules.
/// </summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Func<HttpContext, Task>> _scripted = new();

    private WebhookReceiver(WebApplication app) => _app = app;

    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    public string BaseUrl { get; private set; } = "";

    public string Url(string path = "/hooks") => BaseUrl + path;

    public static async Task<WebhookReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var receiver = new WebhookReceiver(app);

        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            receiver.Received.Enqueue(new ReceivedWebhook(
                context.Request.Path,
                body,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));

            if (receiver._scripted.TryDequeue(out var respond))
            {
                await respond(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        await app.StartAsync();
        receiver.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return receiver;
    }

    /// <summary>Answer the next call with this status code.</summary>
    public void RespondNext(HttpStatusCode status, string body = "") =>
        _scripted.Enqueue(async ctx =>
        {
            ctx.Response.StatusCode = (int)status;
            await ctx.Response.WriteAsync(body);
        });

    public void RedirectNext(string location) =>
        _scripted.Enqueue(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            ctx.Response.Headers.Location = location;
            return Task.CompletedTask;
        });

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
