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

        group.MapGet("satisfied", ListSatisfied)
            .WithName("ListSatisfiedTrackedTitles")
            .Produces<IReadOnlyList<TrackedCollectionTitleDto>>();

        group.MapGet("missing", ListMissing)
            .WithName("ListMissingTrackedTitles")
            .Produces<IReadOnlyList<TrackedCollectionTitleDto>>();

        group.MapGet("upgrades", ListUpgrades)
            .WithName("ListTrackedTitleUpgrades")
            .Produces<IReadOnlyList<TrackedCollectionTitleDto>>();

        group.MapGet("stats", GetStats)
            .WithName("GetTrackedCollectionStats")
            .Produces<TrackedCollectionStatsDto>();

        return app;
    }

    private static async Task<IResult> ListSatisfied(
        IQueryHandler<ListSatisfiedTrackedTitlesQuery, IReadOnlyList<TrackedCollectionTitleDto>> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListSatisfiedTrackedTitlesQuery(), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> ListMissing(
        IQueryHandler<ListMissingTrackedTitlesQuery, IReadOnlyList<TrackedCollectionTitleDto>> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListMissingTrackedTitlesQuery(), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> ListUpgrades(
        IQueryHandler<ListTrackedTitleUpgradesQuery, IReadOnlyList<TrackedCollectionTitleDto>> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListTrackedTitleUpgradesQuery(), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> GetStats(
        IQueryHandler<GetTrackedCollectionStatsQuery, TrackedCollectionStatsDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetTrackedCollectionStatsQuery(), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }
}
