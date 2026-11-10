using Foxfire.Server.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoxfireData();

var app = builder.Build();

await app.Services.MigrateFoxfireDatabaseAsync();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .ExcludeFromDescription();

// Readiness reports only Healthy/Unhealthy — never check details.
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

await app.RunAsync();

public partial class Program;
