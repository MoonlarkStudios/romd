using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Dashboard.Queries.GetCoverageStats;
using Romd.Admin.Application.Dashboard.Queries.GetStorageStats;
using Romd.Admin.Application.Dashboard.Queries.GetSystemHealth;
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
        IQueryHandler<GetStorageStatsQuery, StorageStatsDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetStorageStatsQuery(), ct);
        return result.Match(
            stats => Results.Ok(stats),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetCoverage(
        IQueryHandler<GetCoverageStatsQuery, CoverageStatsDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetCoverageStatsQuery(), ct);
        return result.Match(
            stats => Results.Ok(stats),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetHealth(
        IQueryHandler<GetSystemHealthQuery, SystemHealthDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetSystemHealthQuery(), ct);
        return result.Match(
            stats => Results.Ok(stats),
            errors => errors.ToProblem());
    }
}
