using Foxfire.Server;
using Foxfire.Server.Applications;
using Foxfire.Server.Auth;
using Foxfire.Server.Data;
using Foxfire.Server.Media;
using Foxfire.Server.Processing;
using Foxfire.Server.Security;
using Foxfire.Server.Storage;
using Foxfire.Server.Streaming;
using Foxfire.Server.Uploads;
using Foxfire.Server.Videos;
using Foxfire.Server.Webhooks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Optional operator-supplied config (application list, webhook URLs). Mounted at /app/config in Docker.
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "config", "appsettings.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// The hosting diagnostics logger writes full request URLs; keep it quiet regardless of appsettings.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddFoxfireData();
builder.Services.AddFoxfireApplications();
builder.Services.AddFoxfireAuth();
builder.Services.AddFoxfireStorage();
builder.Services.AddFoxfireVideos();
builder.Services.AddFoxfireTokens();
builder.Services.AddFoxfireUploads();
builder.Services.AddFoxfireMedia();
builder.Services.AddFoxfireProcessing();
builder.Services.AddFoxfireWebhooks();
builder.Services.AddFoxfireStreaming();

var app = builder.Build();

// Fail fast on invalid configuration before touching the database.
_ = app.Services.GetRequiredService<ApplicationCatalog>();
_ = app.Services.GetRequiredService<SigningKeys>();
_ = app.Services.GetRequiredService<PublicUrls>();
_ = app.Services.GetRequiredService<IMediaStorage>(); // creates the storage layout
await app.Services.MigrateFoxfireDatabaseAsync();
await app.Services.SyncApplicationsAsync();
await app.Services.ResumePendingDeletesAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<ApiKeyGuardMiddleware>();
app.UseUploadEndpointMiddleware();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .ExcludeFromDescription();

// Readiness reports only Healthy/Unhealthy — never check details.
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapAppApi()
    .MapAppVideos()
    .MapAppUploads();

app.MapTusUploads();
app.MapStreaming();

await app.RunAsync();

public partial class Program;
