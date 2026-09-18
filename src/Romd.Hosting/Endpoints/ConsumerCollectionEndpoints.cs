using Romd.Application.Common.ReferenceCatalog;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Collections.Queries.GetConsumerCollection;
using Romd.Consumer.Application.Collections.Queries.ListConsumerCollections;
using Romd.Consumer.Application.Collections.Queries.ListConsumerCollectionTitles;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Collections;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerCollectionEndpoints
{
    public static IEndpointRouteBuilder MapConsumerCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("collections")
            .WithTags("Consumer Collections")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("", ListCollections)
            .WithName("ListConsumerCollections")
            .Produces<IReadOnlyList<ConsumerCollectionDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("{collectionId}", GetCollection)
            .WithName("GetConsumerCollection")
            .Produces<ConsumerCollectionDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("{collectionId}/titles", ListCollectionTitles)
            .WithName("ListConsumerCollectionTitles")
            .Produces<Page<ConsumerCollectionTitleDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListCollections(
        string? systemKey,
        [FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<ListConsumerCollectionsQuery, IReadOnlyList<ConsumerCollectionDto>> handler,
        CancellationToken ct)
    {
        int? platformId = null;
        if (systemKey is not null)
        {
            platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, ct);
            if (platformId is null) return Results.NotFound();
        }

        var query = new ListConsumerCollectionsQuery(platformId);
        var result = await handler.HandleAsync(query, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetCollection(
        Sqid collectionId,
        IQueryHandler<GetConsumerCollectionQuery, ConsumerCollectionDto> handler,
        CancellationToken ct)
    {
        var query = new GetConsumerCollectionQuery(collectionId);
        var result = await handler.HandleAsync(query, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> ListCollectionTitles(
        Sqid collectionId,
        string? cursor,
        int? limit,
        IQueryHandler<ListConsumerCollectionTitlesQuery, Page<ConsumerCollectionTitleDto>> handler,
        CancellationToken ct)
    {
        var query = new ListConsumerCollectionTitlesQuery(collectionId, cursor, limit ?? 50);
        var result = await handler.HandleAsync(query, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }
}
