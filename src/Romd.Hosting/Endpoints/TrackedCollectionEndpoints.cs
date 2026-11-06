using Romd.Admin.Application.TrackedCollection.Queries.ListTrackedTitles;
using System.ComponentModel.DataAnnotations;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.TrackedCollection;
using Romd.Contracts.Common.Models;
using Romd.Admin.Application.TrackedCollection.Queries.GetTrackedCollectionStats;
using Romd.Admin.Application.TrackedCollection.Queries.ListMissingTrackedTitles;
using Romd.Admin.Application.TrackedCollection.Queries.ListSatisfiedTrackedTitles;
using Romd.Admin.Application.TrackedCollection.Queries.ListTrackedTitleUpgrades;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.TrackedCollection;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class TrackedCollectionEndpoints
{
    public static IEndpointRouteBuilder MapTrackedCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tracked-titles")
            .WithTags("Tracked Collection")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("", List).WithName("ListTrackedTitles").Produces<Page<TrackedCollectionTitleDto>>().ProducesProblems(400);

        group.MapGet("stats", GetStats)
            .WithName("GetTrackedCollectionStats")
            .Produces<TrackedCollectionStatsDto>();

        return app;
    }

    private static async Task<IResult> List(IQueryHandler<ListTrackedTitlesQuery, Page<TrackedCollectionTitleDto>> handler,
        [ClosedValues("satisfied", "missing", "upgrades")] string? status, string? query, string? cursor,
        CancellationToken ct, [Range(1, 100)] int limit = 50)
    {
        var view = status switch { "satisfied" => TrackedCollectionView.Satisfied, "upgrades" => TrackedCollectionView.Upgrades, _ => TrackedCollectionView.Missing };
        return (await handler.HandleAsync(new(view, query, cursor, limit), ct)).Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> GetStats(
        IQueryHandler<GetTrackedCollectionStatsQuery, TrackedCollectionStatsDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetTrackedCollectionStatsQuery(), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }
}
