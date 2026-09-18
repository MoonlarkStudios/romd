using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Dashboard.Queries.GetSystemStats;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for system-wide operations.
/// </summary>
public static class SystemEndpoints
{
    /// <summary>
    ///     Maps all system-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/system")
            .WithTags("System");

        group.MapGet("stats", GetStats)
            .WithName("GetSystemStats")
            .WithDescription("Get unified system dashboard statistics")
            .Produces<SystemStats>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static async Task<IResult> GetStats(
        IQueryHandler<GetSystemStatsQuery, SystemStats> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetSystemStatsQuery(), ct);

        return result.Match(
            stats => Results.Ok(stats),
            errors => errors.ToProblem());
    }
}
