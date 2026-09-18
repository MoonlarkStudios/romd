using Romd.Application.Common.ReferenceCatalog;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Browse.Queries.GetConsumerPlatform;
using Romd.Consumer.Application.Browse.Queries.GetConsumerTitle;
using Romd.Consumer.Application.Browse.Queries.ListConsumerPlatforms;
using Romd.Consumer.Application.Browse.Queries.SearchConsumerCatalog;
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
            .Produces<CommonModels.Page<ConsumerPlatformSummaryDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("me/library/systems/{systemKey}", GetPlatform)
            .WithName("GetConsumerLibrarySystem")
            .Produces<ConsumerPlatformDetailDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("catalog", SearchCatalog)
            .WithName("SearchConsumerCatalog")
            .Produces<CommonModels.Page<ConsumerTitleCardDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("titles/{titleId}", GetTitle)
            .WithName("GetConsumerTitle")
            .Produces<ConsumerTitleDetailDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListPlatforms(
        IQueryHandler<ListConsumerPlatformsQuery, CommonModels.Page<ConsumerPlatformSummaryDto>> handler,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new ListConsumerPlatformsQuery(cursor, Math.Clamp(limit, 1, 100)),
            cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetPlatform(
        string systemKey,
        [FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetConsumerPlatformQuery, ConsumerPlatformDetailDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetConsumerPlatformQuery(await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> SearchCatalog(
        IQueryHandler<SearchConsumerCatalogQuery, CommonModels.Page<ConsumerTitleCardDto>> handler,
        [FromServices] IReferenceCatalogService referenceCatalog,
        string? query = null,
        string? systemKey = null,
        string? genre = null,
        string? completeness = null,
        string? sortBy = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new SearchConsumerCatalogQuery(
                query,
                systemKey is null ? (int?)null : await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0,
                genre,
                completeness,
                sortBy,
                cursor,
                Math.Clamp(limit, 1, 100)),
            cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetTitle(
        Sqid titleId,
        IQueryHandler<GetConsumerTitleQuery, ConsumerTitleDetailDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetConsumerTitleQuery(titleId), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }
}
