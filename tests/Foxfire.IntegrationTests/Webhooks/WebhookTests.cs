using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foxfire.IntegrationTests.Infrastructure;
using Foxfire.Server.Data;
using Foxfire.Server.Media;
using Foxfire.Server.Videos;
using Foxfire.Server.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Webhooks;

public sealed class WebhookTests
{
    private sealed record Harness(FoxfireFactory Factory, WebhookReceiver Receiver, HttpClient App, string Key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            App.Dispose();
            await Factory.DisposeAsync();
            await Receiver.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(string? secondaryKey = null, bool manualDispatch = false)
    {
        var receiver = await WebhookReceiver.StartAsync();
        var factory = new FoxfireFactory();
        await factory.InitializeAsync();
        if (manualDispatch)
        {
            factory.Settings["Webhooks:DispatchInBackground"] = "false";
        }

        var key = factory.AddApp("AppA", webhookUrl: receiver.Url(), secondaryKey: secondaryKey);
        factory.AddApp("NoHook");
        return new Harness(factory, receiver, factory.CreateAppClient(key), key);
    }

    private static async Task<string> UploadAsync(Harness h, int size = 64)
    {
        var ticket = await TusClient.MintAsync(h.App, sizeBytes: size);
        var tus = new TusClient(h.Factory.CreateClient(), ticket.UploadToken);
        var location = await tus.CreateOrThrowAsync(size);
        Assert.Equal(HttpStatusCode.NoContent, (await tus.PatchAsync(location, 0, new byte[size])).StatusCode);
        return ticket.VideoId;
    }

    private static Task WaitForTypesAsync(WebhookReceiver receiver, params string[] types) =>
        TestData.WaitUntilAsync(
            () => Task.FromResult(types.All(t => receiver.Received.Any(r => r.Headers.GetValueOrDefault("Foxfire-Event-Type") == t))),
            "webhooks " + string.Join(", ", types));

    private static Task<int> DispatchAsync(FoxfireFactory factory) =>
        factory.Services.GetRequiredService<WebhookDispatcher>().DeliverDueAsync(CancellationToken.None);

    private static async Task<List<WebhookDelivery>> DeliveriesAsync(FoxfireFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<WebhookDelivery>().AsNoTracking().OrderBy(d => d.CreatedAt).ToListAsync();
    }

    private static string ExpectedSignature(string key, string timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    [Fact]
    public async Task The_upload_lifecycle_is_announced_in_order_and_signed_with_the_apps_key()
    {
        await using var h = await StartAsync();

        var videoId = await UploadAsync(h);
        await WaitForTypesAsync(h.Receiver, WebhookEventTypes.UploadStarted, WebhookEventTypes.UploadCompleted, WebhookEventTypes.VideoProcessing, WebhookEventTypes.VideoReady);

        var received = h.Receiver.Received.ToList();
        Assert.Equal(
            [WebhookEventTypes.UploadStarted, WebhookEventTypes.UploadCompleted, WebhookEventTypes.VideoProcessing, WebhookEventTypes.VideoReady],
            received.Select(r => r.Headers["Foxfire-Event-Type"]));

        foreach (var hook in received)
        {
            var parts = hook.Headers["Foxfire-Signature"].Split(',');
            var timestamp = parts.Single(p => p.StartsWith("t=", StringComparison.Ordinal))[2..];
            var signature = parts.Single(p => p.StartsWith("v1=", StringComparison.Ordinal))[3..];
            Assert.Equal(ExpectedSignature(h.Key, timestamp, hook.Body), signature);
            Assert.StartsWith("evt_", hook.Headers["Foxfire-Event-Id"], StringComparison.Ordinal);
            Assert.StartsWith("application/json", hook.Headers["Content-Type"], StringComparison.Ordinal);

            using var json = JsonDocument.Parse(hook.Body);
            Assert.Equal(hook.Headers["Foxfire-Event-Id"], json.RootElement.GetProperty("id").GetString());
            Assert.Equal(videoId, json.RootElement.GetProperty("data").GetProperty("videoId").GetString());
            Assert.DoesNotContain(h.Key, hook.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("v1.", hook.Body, StringComparison.Ordinal); // no tokens of any kind
        }

        using var ready = JsonDocument.Parse(received[^1].Body);
        Assert.Equal("Ready", ready.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(12_000, ready.RootElement.GetProperty("data").GetProperty("durationMs").GetInt64());
        Assert.All(await DeliveriesAsync(h.Factory), d => Assert.Equal(DeliveryStatus.Succeeded, d.Status));
    }

    [Fact]
    public async Task Failures_and_deletes_are_announced()
    {
        await using var h = await StartAsync();
        for (var i = 0; i < 3; i++)
        {
            h.Factory.Media.FailNext(new MediaProcessingException("broken"));
        }

        var videoId = await UploadAsync(h);
        await WaitForTypesAsync(h.Receiver, WebhookEventTypes.VideoFailed);
        Assert.Equal(HttpStatusCode.NoContent, (await h.App.DeleteAsync($"/api/v1/videos/{videoId}")).StatusCode);
        await WaitForTypesAsync(h.Receiver, WebhookEventTypes.VideoDeleted);

        var failed = h.Receiver.Received.Single(r => r.Headers["Foxfire-Event-Type"] == WebhookEventTypes.VideoFailed);
        using var json = JsonDocument.Parse(failed.Body);
        Assert.Equal("broken", json.RootElement.GetProperty("data").GetProperty("error").GetString());
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_on_schedule_then_marked_dead()
    {
        await using var h = await StartAsync(manualDispatch: true);
        for (var i = 0; i < WebhookDispatcher.MaxAttempts; i++)
        {
            h.Receiver.RespondNext(HttpStatusCode.InternalServerError, "receiver exploded");
        }

        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<WebhookEvents>();
            events.RaiseRaw(await h.Factory.AppIdAsync("AppA"), WebhookEventTypes.Test, new { hello = "world" });
            await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().SaveChangesAsync();
        }

        Assert.Equal(1, await DispatchAsync(h.Factory));
        var first = (await DeliveriesAsync(h.Factory)).Single();
        Assert.Equal(DeliveryStatus.Pending, first.Status);
        Assert.Equal(500, first.LastStatusCode);
        Assert.Contains("receiver exploded", first.LastError, StringComparison.Ordinal);
        Assert.Equal(h.Factory.Time.GetUtcNow() + TimeSpan.FromMinutes(1), first.NextAttemptAt);

        foreach (var wait in WebhookDispatcher.RetrySchedule)
        {
            h.Factory.Time.Advance(wait);
            Assert.Equal(1, await DispatchAsync(h.Factory));
        }

        var dead = (await DeliveriesAsync(h.Factory)).Single();
        Assert.Equal(DeliveryStatus.Dead, dead.Status);
        Assert.Equal(WebhookDispatcher.MaxAttempts, dead.Attempt);
        Assert.Equal(WebhookDispatcher.MaxAttempts, h.Receiver.Received.Count);
        Assert.Single(h.Receiver.Received.Select(r => r.Headers["Foxfire-Event-Id"]).Distinct()); // same event id every time
    }

    [Fact]
    public async Task A_recovering_receiver_gets_the_event_on_retry()
    {
        await using var h = await StartAsync(manualDispatch: true);
        h.Receiver.RespondNext(HttpStatusCode.ServiceUnavailable);
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<WebhookEvents>().RaiseRaw(await h.Factory.AppIdAsync("AppA"), WebhookEventTypes.Test, new { });
            await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().SaveChangesAsync();
        }

        Assert.Equal(1, await DispatchAsync(h.Factory));
        h.Factory.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await DispatchAsync(h.Factory));

        var delivery = (await DeliveriesAsync(h.Factory)).Single();
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(2, delivery.Attempt);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        await using var h = await StartAsync(manualDispatch: true);
        h.Receiver.RedirectNext(h.Receiver.Url("/elsewhere"));
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<WebhookEvents>().RaiseRaw(await h.Factory.AppIdAsync("AppA"), WebhookEventTypes.Test, new { });
            await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().SaveChangesAsync();
        }

        Assert.Equal(1, await DispatchAsync(h.Factory));

        var delivery = (await DeliveriesAsync(h.Factory)).Single();
        Assert.Equal(307, delivery.LastStatusCode);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.DoesNotContain(h.Receiver.Received, r => r.Path == "/elsewhere");
    }

    [Fact]
    public async Task Secondary_key_adds_a_second_signature_for_rotation()
    {
        var secondary = FoxfireFactory.NewKey();
        await using var h = await StartAsync(secondaryKey: secondary);

        await UploadAsync(h);
        await WaitForTypesAsync(h.Receiver, WebhookEventTypes.UploadStarted);

        var hook = h.Receiver.Received.First();
        var parts = hook.Headers["Foxfire-Signature"].Split(',');
        var timestamp = parts[0][2..];
        var signatures = parts.Where(p => p.StartsWith("v1=", StringComparison.Ordinal)).Select(p => p[3..]).ToList();
        Assert.Equal([ExpectedSignature(h.Key, timestamp, hook.Body), ExpectedSignature(secondary, timestamp, hook.Body)], signatures);
    }

    [Fact]
    public async Task Apps_without_a_webhook_get_no_outbox_rows()
    {
        await using var h = await StartAsync();
        using var noHook = h.Factory.CreateAppClient(h.Factory.Settings["ApiKeys:NoHook"]!);

        var ticket = await TusClient.MintAsync(noHook, sizeBytes: 8);
        var tus = new TusClient(h.Factory.CreateClient(), ticket.UploadToken);
        await tus.PatchAsync(await tus.CreateOrThrowAsync(8), 0, new byte[8]);
        Guid id;
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            id = await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().Set<Video>()
                .Where(v => v.PublicId == ticket.VideoId).Select(v => v.Id).SingleAsync();
        }
        await h.Factory.WaitForStatusAsync(id, VideoStatus.Ready);

        Assert.DoesNotContain(await DeliveriesAsync(h.Factory), d => d.VideoId == id);
    }

    [Fact]
    public async Task Stalled_and_expired_uploads_are_announced()
    {
        await using var h = await StartAsync();
        var expired = await TusClient.MintAsync(h.App, sizeBytes: 8);
        var stalled = await TusClient.MintAsync(h.App, sizeBytes: 8);
        await new TusClient(h.Factory.CreateClient(), stalled.UploadToken).CreateOrThrowAsync(8);

        h.Factory.Time.Advance(TimeSpan.FromMinutes(16));
        await h.Factory.Services.GetRequiredService<Server.Uploads.UploadSweeper>().SweepAsync(CancellationToken.None);

        await WaitForTypesAsync(h.Receiver, WebhookEventTypes.UploadExpired, WebhookEventTypes.UploadFailed);
        Assert.Contains(h.Receiver.Received, r => r.Headers["Foxfire-Event-Type"] == WebhookEventTypes.UploadExpired && r.Body.Contains(expired.VideoId, StringComparison.Ordinal));
        Assert.Contains(h.Receiver.Received, r => r.Headers["Foxfire-Event-Type"] == WebhookEventTypes.UploadFailed && r.Body.Contains(stalled.VideoId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pending_events_for_a_missing_app_are_never_sent()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        var connectionString = await PostgresServer.CreateDatabaseConnectionStringAsync();

        await using (var first = new FoxfireFactory { ConnectionString = connectionString })
        {
            first.AddApp("AppA", webhookUrl: receiver.Url());
            first.Settings["Webhooks:DispatchInBackground"] = "false";
            await first.InitializeAsync();
            receiver.RespondNext(HttpStatusCode.InternalServerError);
            await using var scope = first.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<WebhookEvents>().RaiseRaw(await first.AppIdAsync("AppA"), WebhookEventTypes.Test, new { });
            await scope.ServiceProvider.GetRequiredService<FoxfireDbContext>().SaveChangesAsync();
            Assert.Equal(1, await DispatchAsync(first));
        }

        await using var second = new FoxfireFactory { ConnectionString = connectionString };
        second.AddApp("Other");
        second.Settings["Webhooks:DispatchInBackground"] = "false";
        _ = second.Server;
        second.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await DispatchAsync(second));

        var delivery = (await DeliveriesAsync(second)).Single();
        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        Assert.Single(receiver.Received);
    }
}
