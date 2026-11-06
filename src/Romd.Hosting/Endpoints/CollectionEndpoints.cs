using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Collections;
using Romd.Admin.Application.Collections.Commands.AddTitle;
using Romd.Admin.Application.Collections.Commands.CreateCollection;
using Romd.Admin.Application.Collections.Commands.DeleteCollection;
using Romd.Admin.Application.Collections.Commands.RemoveTitle;
using Romd.Admin.Application.Collections.Commands.ReorderItems;
using Romd.Admin.Application.Collections.Commands.UpdateCollection;
using Romd.Admin.Application.Collections.Commands.UpdateItemNote;
using Romd.Admin.Application.Collections.Queries.GetCollectionDetail;
using Romd.Admin.Application.Collections.Queries.ListCollections;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Collections;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class CollectionEndpoints
{
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/collections")
            .WithTags("Collections");

        group.MapGet("", ListCollections)
            .WithName("ListCollections")
            .Produces<Page<CollectionSummary>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("", CreateCollection)
            .WithName("CreateCollection")
            .ProducesCreated<CollectionSummary>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapGet("{collectionId}", GetCollectionDetail)
            .WithName("GetCollection")
            .Produces<CollectionDetail>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPut("{collectionId}", UpdateCollection)
            .WithName("UpdateCollection")
            .Produces<CollectionSummary>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapDelete("{collectionId}", DeleteCollection)
            .WithName("DeleteCollection")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("{collectionId}/items", ListItems).WithName("ListCollectionItems").Produces<Page<CollectionItemDto>>().ProducesProblems(400, 404).RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("{collectionId}/items/{titleId}", GetItem).WithName("GetCollectionItem").Produces<CollectionItemDto>().ProducesProblems(404).RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("{collectionId}/items", AddItem)
            .WithName("CreateCollectionItem")
            .ProducesCreated<CollectionItemDto>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapDelete("{collectionId}/items/{titleId}", RemoveItem)
            .WithName("DeleteCollectionItem")
            .Produces<CollectionDetail>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPut("{collectionId}/item-order", ReorderItems)
            .WithName("ReplaceCollectionItemOrder")
            .Produces<CollectionDetail>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPatch("{collectionId}/items/{titleId}/note", UpdateItemNote)
            .WithName("UpdateCollectionItemNote")
            .Produces<CollectionDetail>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        return app;
    }

    private static async Task<IResult> ListCollections(
        SystemKeyParameter? systemKey, string? query, string? cursor,
        IQueryHandler<ListCollectionsQuery, Page<CollectionSummary>> handler,
        CancellationToken ct, [Range(1, 100)] int limit = 50)
    {
        var request = new ListCollectionsQuery(systemKey?.SystemId, query, cursor, limit);
        var result = await handler.HandleAsync(request, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> ListItems(Sqid collectionId, string? cursor, ICollectionRepository repository,
        IReferenceCatalogService referenceCatalog, CancellationToken ct, [Range(1, 100)] int limit = 50)
    {
        var result = await repository.GetItemPageAsync(collectionId.Value, cursor, limit, ct);
        if (result.IsError)
            return result.Errors.ToProblem();
        var keys = await referenceCatalog.GetSystemKeysAsync(ct);
        return Results.Ok(new Page<CollectionItemDto> { Items = result.Value.Items.ToItemDtos(keys), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage });
    }

    private static async Task<IResult> GetItem(Sqid collectionId, Sqid titleId, ICollectionRepository repository,
        IReferenceCatalogService referenceCatalog, CancellationToken ct)
    {
        if (await repository.GetByIdAsync(collectionId.Value, ct) is null)
            return new[] { CollectionErrors.NotFound() }.ToProblem();
        var item = await repository.GetItemDetailAsync(collectionId.Value, titleId.Value, ct);
        return item is null ? new[] { CollectionErrors.TitleNotInCollection() }.ToProblem() : Results.Ok(item.ToItemDto(await referenceCatalog.GetSystemKeysAsync(ct)));
    }

    private static async Task<IResult> CreateCollection([FromServices] IReferenceCatalogService referenceCatalog,
        CreateCollectionRequest request,
        ICommandHandler<CreateCollectionCommand, CollectionSummary> handler,
        CancellationToken ct)
    {
        var (platformProblem, platformId) = await referenceCatalog.ResolveOptionalBodySystemKeyAsync(request.SystemKey, "systemKey", ct);
        if (platformProblem is not null)
            return platformProblem;

        if (RequestBodyIds.DecodeOptional(request.CoverMediaId, "coverMediaId",
                "Collections.InvalidCoverMediaId", out int? coverMediaId) is { } coverMediaProblem)
            return coverMediaProblem;

        var command = new CreateCollectionCommand(
            request.Name,
            request.Description,
            coverMediaId,
            platformId);

        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Created($"/api/collections/{ok.Id}", ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetCollectionDetail(Sqid collectionId,
        IQueryHandler<GetCollectionDetailQuery, CollectionDetail> handler,
        CancellationToken ct)
    {
        var query = new GetCollectionDetailQuery(collectionId);
        var result = await handler.HandleAsync(query, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> UpdateCollection([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid collectionId,
        UpdateCollectionRequest request,
        ICommandHandler<UpdateCollectionCommand, CollectionSummary> handler,
        CancellationToken ct)
    {
        var (platformProblem, platformId) = await referenceCatalog.ResolveOptionalBodySystemKeyAsync(request.SystemKey, "systemKey", ct);
        if (platformProblem is not null)
            return platformProblem;

        if (RequestBodyIds.DecodeOptional(request.CoverMediaId, "coverMediaId",
                "Collections.InvalidCoverMediaId", out int? coverMediaId) is { } coverMediaProblem)
            return coverMediaProblem;

        var command = new UpdateCollectionCommand(
            collectionId,
            request.Name,
            request.Description,
            coverMediaId,
            platformId);

        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> DeleteCollection(Sqid collectionId,
        ICommandHandler<DeleteCollectionCommand, Deleted> handler,
        CancellationToken ct)
    {
        var command = new DeleteCollectionCommand(collectionId);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> AddItem(Sqid collectionId,
        AddCollectionItemRequest request,
        ICommandHandler<AddTitleToCollectionCommand, CollectionItemDto> handler,
        CancellationToken ct)
    {
        if (RequestBodyIds.Decode(request.TitleId, "titleId", "Collections.InvalidTitleId", out int titleId) is { } problem)
            return problem;

        var command = new AddTitleToCollectionCommand(collectionId, titleId, request.Note);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            item => Results.Created($"/api/collections/{collectionId}/items/{IdCoder.Encode(titleId)}", item),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> RemoveItem(Sqid collectionId,
        Sqid titleId,
        ICommandHandler<RemoveTitleFromCollectionCommand, CollectionDetail> handler,
        CancellationToken ct)
    {
        var command = new RemoveTitleFromCollectionCommand(collectionId, titleId);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> ReorderItems(Sqid collectionId,
        ReorderCollectionItemsRequest request,
        ICommandHandler<ReorderCollectionItemsCommand, CollectionDetail> handler,
        CancellationToken ct)
    {
        if (RequestBodyIds.DecodeAll(request.TitleIds, "titleIds", "Collections.InvalidTitleId",
                out IReadOnlyList<int> titleIds) is { } problem)
            return problem;

        var command = new ReorderCollectionItemsCommand(collectionId, titleIds);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> UpdateItemNote(Sqid collectionId,
        Sqid titleId,
        UpdateCollectionItemNoteRequest request,
        ICommandHandler<UpdateCollectionItemNoteCommand, CollectionDetail> handler,
        CancellationToken ct)
    {
        var command = new UpdateCollectionItemNoteCommand(collectionId, titleId, request.Note);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }
}
