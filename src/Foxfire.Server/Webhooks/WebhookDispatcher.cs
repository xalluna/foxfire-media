using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Webhooks;

/// <summary>
/// Delivers outbox rows. Each request is signed with the receiving application's own API key:
/// <c>Foxfire-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(key, "t.body")&gt;</c> (a second v1 is
/// included while a secondary key is configured). Redirects are never followed; any 2xx is success;
/// failures retry at 1m, 5m, 30m, 2h, 6h and 12h, then the delivery is marked Dead.
/// Events for one video are delivered in order.
/// </summary>
public sealed partial class WebhookDispatcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    ApplicationCatalog catalog,
    WebhookSignal signal,
    TimeProvider time,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    public const string HttpClientName = "webhooks";
    public const int MaxAttempts = 7;
    private const int BatchSize = 20;
    private const int MaxErrorLength = 500;

    public static readonly TimeSpan[] RetrySchedule =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
    ];

    /// <summary>While a delivery is in flight its next attempt is pushed out by this much (crash safety).</summary>
    private static readonly TimeSpan InFlightLease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Webhooks:DispatchInBackground (default true). Tests turn it off to drive retries deterministically.</summary>
    public bool RunInBackground { get; init; } = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RunInBackground)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DeliverDueAsync(stoppingToken) == 0)
                {
                    await signal.WaitAsync(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLoopError(logger, ex);
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Sends every delivery that is due. Returns how many were attempted.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoxfireDbContext>();
        var now = time.GetUtcNow();

        var lease = now + InFlightLease;

        // Claim atomically (in-flight lease), so overlapping passes never send a delivery twice.
        // Per-video ordering: a delivery waits while an older event for the same video is still pending.
        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE webhook_deliveries SET next_attempt_at = {lease}
            WHERE id IN (
                SELECT d.id FROM webhook_deliveries d
                WHERE d.status = 'Pending' AND d.next_attempt_at <= {now}
                  AND (d.video_id IS NULL OR NOT EXISTS (
                      SELECT 1 FROM webhook_deliveries o
                      WHERE o.video_id = d.video_id AND o.application_id = d.application_id
                        AND o.status = 'Pending' AND o.created_at < d.created_at))
                ORDER BY d.created_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (claimed.Count == 0)
        {
            return 0;
        }

        var due = await db.Set<WebhookDelivery>().Where(d => claimed.Contains(d.Id)).OrderBy(d => d.CreatedAt).ToListAsync(ct);

        var appIds = due.Select(d => d.ApplicationId).Distinct().ToList();
        var apps = await db.Set<Application>().AsNoTracking().Where(a => appIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        foreach (var delivery in due)
        {
            await SendAsync(delivery, apps.GetValueOrDefault(delivery.ApplicationId), ct);
            await db.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    private async Task SendAsync(WebhookDelivery delivery, Application? app, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        delivery.UpdatedAt = now;
        var configured = app is null ? null : catalog.Find(app.Name);
        if (app is null || app.Status != ApplicationStatus.Active || configured?.WebhookUrl is null)
        {
            delivery.Status = DeliveryStatus.Dead;
            delivery.LastError = "The application or its webhook is no longer configured.";
            return;
        }

        delivery.Attempt++;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, configured.WebhookUrl)
            {
                Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json"),
            };
            var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            request.Headers.Add("Foxfire-Event-Id", delivery.EventId);
            request.Headers.Add("Foxfire-Event-Type", delivery.Type);
            request.Headers.Add("Foxfire-Signature", Signature(timestamp, delivery.Payload, configured));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FoxfireMedia-Webhooks", "1"));

            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            delivery.LastStatusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = DeliveryStatus.Succeeded;
                delivery.DeliveredAt = now;
                delivery.LastError = null;
                return;
            }

            var snippet = await ReadSnippetAsync(response, ct);
            Retry(delivery, now, $"HTTP {(int)response.StatusCode}{(snippet.Length > 0 ? ": " + snippet : "")}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            delivery.LastStatusCode = null;
            Retry(delivery, now, ex is TaskCanceledException ? "Timed out." : "Connection failed: " + ex.Message);
        }
    }

    /// <summary>Signature header value. Exposed for the receiver-side verification snippet and tests.</summary>
    public static string Signature(string timestamp, string body, ConfiguredApplication app)
    {
        var parts = new List<string> { $"t={timestamp}", "v1=" + Sign(app.ApiKey.RevealUtf8(), timestamp, body) };
        if (app.SecondaryApiKey is { } secondary)
        {
            parts.Add("v1=" + Sign(secondary.RevealUtf8(), timestamp, body));
        }

        return string.Join(',', parts);
    }

    private static string Sign(byte[] key, string timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    private static void Retry(WebhookDelivery delivery, DateTimeOffset now, string error)
    {
        delivery.LastError = error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
        if (delivery.Attempt >= MaxAttempts)
        {
            delivery.Status = DeliveryStatus.Dead;
            return;
        }

        delivery.NextAttemptAt = now + RetrySchedule[Math.Min(delivery.Attempt - 1, RetrySchedule.Length - 1)];
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var buffer = new char[200];
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var read = await reader.ReadBlockAsync(buffer, ct);
            return new string(buffer, 0, read).ReplaceLineEndings(" ").Trim();
        }
        catch (IOException)
        {
            return "";
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook dispatch loop error")]
    private static partial void LogLoopError(ILogger logger, Exception ex);
}

public static class WebhookServiceExtensions
{
    public static IServiceCollection AddFoxfireWebhooks(this IServiceCollection services)
    {
        services.AddSingleton<WebhookSignal>();
        services.AddScoped<WebhookEvents>();
        services.AddScoped<Videos.IVideoDeletionHook, WebhookDeletionHook>();
        services.AddHttpClient(WebhookDispatcher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddSingleton(sp => new WebhookDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ApplicationCatalog>(),
            sp.GetRequiredService<WebhookSignal>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WebhookDispatcher>>())
        {
            RunInBackground = sp.GetRequiredService<IConfiguration>().GetValue("Webhooks:DispatchInBackground", true),
        });
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        return services;
    }
}
