using Romd.Hosting.Dashboard;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dashboard")
            .WithTags("Dashboard");

        group.MapGet("storage", GetStorage)
            .WithName("GetStorageStats")
            .Produces<StorageStatsDto>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("coverage", GetCoverage)
            .WithName("GetCoverageStats")
            .Produces<CoverageStatsDto>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("health", GetHealth)
            .WithName("GetSystemHealth")
            .Produces<SystemHealthDto>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
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
