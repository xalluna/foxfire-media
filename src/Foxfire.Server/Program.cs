using Foxfire.Server.Applications;
using Foxfire.Server.Data;
using Foxfire.Server.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Optional operator-supplied config (application list, webhook URLs). Mounted at /app/config in Docker.
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "config", "appsettings.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// The hosting diagnostics logger writes full request URLs; keep it quiet regardless of appsettings.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

builder.Services.AddFoxfireData();
builder.Services.AddFoxfireApplications();

var app = builder.Build();

// Fail fast on invalid application / key configuration before touching the database.
_ = app.Services.GetRequiredService<ApplicationCatalog>();
await app.Services.MigrateFoxfireDatabaseAsync();

app.UseMiddleware<RequestLoggingMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .ExcludeFromDescription();

// Readiness reports only Healthy/Unhealthy — never check details.
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

await app.RunAsync();

public partial class Program;
