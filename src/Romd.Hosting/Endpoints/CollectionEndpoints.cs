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
            .Produces<IReadOnlyList<CollectionSummary>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("", CreateCollection)
            .WithName("CreateCollection")
            .Produces<CollectionSummary>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapGet("{collectionId}", GetCollectionDetail)
            .WithName("GetCollectionDetail")
            .Produces<CollectionDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPut("{collectionId}", UpdateCollection)
            .WithName("UpdateCollection")
            .Produces<CollectionSummary>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapDelete("{collectionId}", DeleteCollection)
            .WithName("DeleteCollection")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{collectionId}/items", AddItem)
            .WithName("AddCollectionItem")
            .Produces<CollectionDetail>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapDelete("{collectionId}/items/{titleId}", RemoveItem)
            .WithName("RemoveCollectionItem")
            .Produces<CollectionDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPut("{collectionId}/items/reorder", ReorderItems)
            .WithName("ReorderCollectionItems")
            .Produces<CollectionDetail>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPatch("{collectionId}/items/{titleId}/note", UpdateItemNote)
            .WithName("UpdateCollectionItemNote")
            .Produces<CollectionDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        return app;
    }

    private static async Task<IResult> ListCollections([FromServices] IReferenceCatalogService referenceCatalog,
        string? systemKey,
        IQueryHandler<ListCollectionsQuery, IReadOnlyList<CollectionSummary>> handler,
        CancellationToken ct)
    {
        var query = new ListCollectionsQuery((systemKey is null ? (int?)null : await referenceCatalog.RequireSystemAsync(systemKey, ct)));
        var result = await handler.HandleAsync(query, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> CreateCollection([FromServices] IReferenceCatalogService referenceCatalog,
        CreateCollectionRequest request,
        ICommandHandler<CreateCollectionCommand, CollectionSummary> handler,
        CancellationToken ct)
    {
        var (platformId, platformProblem) = await ResolveSystemKey(request.SystemKey, referenceCatalog, ct);
        if (platformProblem is not null)
            return platformProblem;

        var (coverMediaId, coverMediaProblem) = DecodeCoverMediaId(request.CoverMediaId);
        if (coverMediaProblem is not null)
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

    /// <summary>
    ///     Decodes an optional Sqid cover media id. A missing value is valid (no cover); a
    ///     supplied-but-invalid value is a 400 envelope per the strict-validation policy.
    /// </summary>
    private static (int? Value, IResult? Problem) DecodeCoverMediaId(string? coverMediaId)
    {
        if (coverMediaId is null)
            return (null, null);

        if (!IdCoder.TryDecode(coverMediaId, out int value))
        {
            return (null, ProblemResults.ValidationProblem(
                "Collections.InvalidCoverMediaId",
                "coverMediaId",
                $"'{coverMediaId}' is not a valid cover media ID."));
        }

        return (value, null);
    }

    /// <summary>
    ///     Decodes an optional Sqid platform id. A missing value is valid (no platform scope); a
    ///     supplied-but-invalid value is a 400 envelope per the strict-validation policy.
    /// </summary>
    private static async Task<(int? Value, IResult? Problem)> ResolveSystemKey(string? key, [FromServices] IReferenceCatalogService service, CancellationToken ct)
    {
        if (key is null) return (null, null);
        var id = await service.ResolveSystemIdAsync(key, ct);
        return id is null ? (null, ProblemResults.ValidationProblem("Collections.InvalidSystemKey", "systemKey", $"Unknown system key '{key}'.")) : (id, null);
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
        var (platformId, platformProblem) = await ResolveSystemKey(request.SystemKey, referenceCatalog, ct);
        if (platformProblem is not null)
            return platformProblem;

        var (coverMediaId, coverMediaProblem) = DecodeCoverMediaId(request.CoverMediaId);
        if (coverMediaProblem is not null)
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
        ICommandHandler<AddTitleToCollectionCommand, CollectionDetail> handler,
        CancellationToken ct)
    {
        if (!IdCoder.TryDecode(request.TitleId, out var titleId))
        {
            return ProblemResults.ValidationProblem(
                "Collections.InvalidTitleId",
                "titleId",
                $"'{request.TitleId}' is not a valid title ID.");
        }

        var command = new AddTitleToCollectionCommand(collectionId, titleId, request.Note);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Ok(ok),
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
        var titleIds = new List<int>();
        var invalidTitleIds = new List<string>();
        foreach (var sqidStr in request.TitleIds)
        {
            if (!IdCoder.TryDecode(sqidStr, out var tid))
            {
                invalidTitleIds.Add($"'{sqidStr}' is not a valid title ID.");
                continue;
            }

            titleIds.Add(tid);
        }

        if (invalidTitleIds.Count > 0)
        {
            return ProblemResults.ValidationProblem(
                "Collections.InvalidTitleId",
                new Dictionary<string, string[]> { ["titleIds"] = [.. invalidTitleIds] });
        }

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
