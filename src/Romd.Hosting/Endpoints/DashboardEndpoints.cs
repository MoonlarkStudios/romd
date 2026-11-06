using Romd.Hosting.Dashboard;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/server/storage", GetStorage).WithTags("Server").WithName("GetServerStorage")
            .Produces<StorageStatsDto>().RequireAuthorization(AuthorizationPolicies.RequireUser);
        app.MapGet("/catalog/stats/coverage", GetCoverage).WithTags("Catalog").WithName("GetCatalogCoverageStats")
            .Produces<CoverageStatsDto>().RequireAuthorization(AuthorizationPolicies.RequireUser);
        app.MapGet("/server/health", GetHealth).WithTags("Server").WithName("GetServerHealth")
            .Produces<SystemHealthDto>().RequireAuthorization(AuthorizationPolicies.RequireUser);
        app.MapGet("/dashboard/status-bar", GetStatusBar).WithTags("Dashboard").WithName("GetDashboardStatusBar")
            .WithDescription("Current storage, catalog coverage and host health projections.")
            .Produces<DashboardStatusBarDto>().RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static async Task<IResult> GetStatusBar(DashboardStatsCache cache, CancellationToken ct)
    {
        var storage = await cache.GetStorageAsync(ct);
        if (storage.IsError)
            return storage.Errors.ToProblem();
        var coverage = await cache.GetCoverageAsync(ct);
        if (coverage.IsError)
            return coverage.Errors.ToProblem();
        var health = await cache.GetHealthAsync(ct);
        return health.Match<IResult>(value => Results.Ok(new DashboardStatusBarDto(storage.Value, coverage.Value, value)),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetStorage(
        DashboardStatsCache cache,
        CancellationToken ct)
    {
        var result = await cache.GetStorageAsync(ct);
        return result.Match(
            stats => Results.Ok(stats),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetCoverage(
        DashboardStatsCache cache,
        CancellationToken ct)
    {
        var result = await cache.GetCoverageAsync(ct);
        return result.Match(
            stats => Results.Ok(stats),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetHealth(
        DashboardStatsCache cache,
        CancellationToken ct)
    {
        var result = await cache.GetHealthAsync(ct);
        return result.Match(
            stats => Results.Ok(stats),
            errors => errors.ToProblem());
    }
}
