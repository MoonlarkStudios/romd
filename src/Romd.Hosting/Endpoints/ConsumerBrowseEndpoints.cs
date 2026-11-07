using System.ComponentModel.DataAnnotations;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Browse.Queries.GetConsumerPlatform;
using Romd.Consumer.Application.Browse.Queries.GetConsumerTitle;
using Romd.Consumer.Application.Browse.Queries.ListConsumerPlatforms;
using Romd.Consumer.Application.Browse.Queries.SearchConsumerCatalog;
using Romd.Consumer.Application.Browse.Queries.ListConsumerTitleMedia;
using Romd.Consumer.Application.Browse.Queries.ListConsumerTitleReleases;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Consumer.Browse;
using Romd.Host.Authorization;
using CommonModels = Romd.Contracts.Common.Models;

namespace Romd.Host.Endpoints;

public static class ConsumerBrowseEndpoints
{
    public static IEndpointRouteBuilder MapConsumerBrowseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("")
            .WithTags("Consumer Browse")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("me/library/systems", ListPlatforms)
            .WithName("ListConsumerLibrarySystems")
            .Produces<CommonModels.Page<ConsumerPlatformSummary>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        group.MapGet("me/library/systems/{systemKey}", GetPlatform)
            .WithName("GetConsumerLibrarySystem")
            .Produces<ConsumerPlatformDetail>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("catalog", SearchCatalog)
            .WithName("ListConsumerCatalog")
            .Produces<CommonModels.Page<ConsumerTitleCard>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        group.MapGet("titles/{titleId}", GetTitle)
            .WithName("GetConsumerTitle")
            .Produces<ConsumerTitleDetail>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        group.MapGet("titles/{titleId}/media", ListTitleMedia).WithName("ListConsumerTitleMedia")
            .Produces<CommonModels.Page<ConsumerMediaRef>>().Produces(401).ProducesProblems(400, 404);
        group.MapGet("titles/{titleId}/releases", ListTitleReleases).WithName("ListConsumerTitleReleases")
            .Produces<CommonModels.Page<ConsumerRelease>>().Produces(401).ProducesProblems(400, 404);

        return app;
    }

    private static async Task<IResult> ListPlatforms(
        IQueryHandler<ListConsumerPlatformsQuery, CommonModels.Page<ConsumerPlatformSummary>> handler,
        string? cursor = null,
        [Range(1, ListConsumerPlatformsQuery.MaxLimit)] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new ListConsumerPlatformsQuery(cursor, limit),
            cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetPlatform(
        SystemKeyParameter systemKey,
        IQueryHandler<GetConsumerPlatformQuery, ConsumerPlatformDetail> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetConsumerPlatformQuery(systemKey.SystemId), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> SearchCatalog(
        IQueryHandler<SearchConsumerCatalogQuery, CommonModels.Page<ConsumerTitleCard>> handler,
        string? query = null,
        SystemKeyParameter? systemKey = null,
        string? genre = null,
        [ClosedValues("all", "complete", "partial")] string? completeness = null,
        [ClosedValues("name", "rating")] string? sortBy = null,
        string? cursor = null,
        [Range(1, SearchConsumerCatalogQuery.MaxLimit)] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new SearchConsumerCatalogQuery(
                query,
                systemKey?.SystemId,
                genre,
                completeness,
                sortBy,
                cursor,
                limit),
            cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetTitle(
        Sqid titleId,
        IQueryHandler<GetConsumerTitleQuery, ConsumerTitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetConsumerTitleQuery(titleId), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> ListTitleMedia(Sqid titleId,
        IQueryHandler<ListConsumerTitleMediaQuery, CommonModels.Page<ConsumerMediaRef>> handler,
        string? cursor = null, [Range(1, ListConsumerTitleMediaQuery.MaxLimit)] int limit = 50, CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new(titleId, cursor, limit), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> ListTitleReleases(Sqid titleId,
        IQueryHandler<ListConsumerTitleReleasesQuery, CommonModels.Page<ConsumerRelease>> handler,
        string? cursor = null, [Range(1, ListConsumerTitleReleasesQuery.MaxLimit)] int limit = 50, CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new(titleId, cursor, limit), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }
}
