using System.ComponentModel.DataAnnotations;
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
            .Produces<Page<ConsumerCollectionDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        group.MapGet("{collectionId}", GetCollection)
            .WithName("GetConsumerCollection")
            .Produces<ConsumerCollectionDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        group.MapGet("{collectionId}/titles", ListCollectionTitles)
            .WithName("ListConsumerCollectionTitles")
            .Produces<Page<ConsumerCollectionTitleDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListCollections(
        SystemKeyParameter? systemKey,
        IQueryHandler<ListConsumerCollectionsQuery, Page<ConsumerCollectionDto>> handler,
        string? cursor = null,
        [Range(1, ListConsumerCollectionsQuery.MaxLimit)] int limit = 50,
        CancellationToken ct = default)
    {
        var query = new ListConsumerCollectionsQuery(systemKey?.SystemId, cursor, limit);
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
        [Range(1, ListConsumerCollectionTitlesQuery.MaxLimit)] int? limit,
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
