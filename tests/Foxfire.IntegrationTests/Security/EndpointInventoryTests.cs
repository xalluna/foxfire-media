using Foxfire.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Foxfire.IntegrationTests.Security;

/// <summary>
/// Every route the server exposes, with its HTTP methods. Adding a route (especially one that serves
/// files) fails this test until the list is consciously updated — a review checkpoint for "no video
/// data download/passthrough".
/// </summary>
public sealed class EndpointInventoryTests(FoxfireFactory factory) : IClassFixture<FoxfireFactory>
{
    public static readonly string[] Expected =
    [
        "GET /api/v1/me",
        "GET /api/v1/videos/",
        "GET /api/v1/videos/{publicId}",
        "GET /api/v1/videos/by-external-id/{externalId}",
        "PATCH /api/v1/videos/{publicId}",
        "DELETE /api/v1/videos/{publicId}",
        "POST /api/v1/uploads",
        "POST /api/v1/videos/{publicId}/upload-token",
        "POST /api/v1/videos/{publicId}/embed",
        "GET /embed/{token}",
        "GET /poster/{token}",
        "GET /healthz",
        "GET /",
        "GET /admin/{**path}",
        "* /readyz",
        "* /uploads/tus/{TusFileId?}",
        "GET /stream/{token}/{asset}",
        "POST /stream/{token}/refresh",
    ];

    [Fact]
    public void The_route_table_matches_the_reviewed_inventory()
    {
        _ = factory.Server;
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return $"{(methods is { Count: > 0 } ? string.Join(",", methods) : "*")} /{e.RoutePattern.RawText?.TrimStart('/')}";
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Expected.Order(StringComparer.Ordinal), routes);
    }
}
