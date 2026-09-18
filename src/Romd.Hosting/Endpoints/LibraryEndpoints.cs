using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.Libraries.Queries.EvaluateLibrary;
using ErrorOr;
using Romd.Admin.Application.Libraries.Commands.SetLibraryAttachments;
using Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.CreateLibrary;
using Romd.Admin.Application.Libraries.Commands.DeleteLibrary;
using Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;
using Romd.Admin.Application.Libraries.Commands.UpdateLibrary;
using Romd.Admin.Application.Libraries.Queries.GetLibraryById;
using Romd.Admin.Application.Libraries.Queries.GetLibraryCollections;
using Romd.Admin.Application.Libraries.Queries.GetLibraryGenreFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryPlatformFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryTitleReleaseDiagnostics;
using Romd.Admin.Application.Libraries.Queries.ListLibraries;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Romd.Host.Authorization;
using JobReference = Romd.Contracts.Management.Models.JobReference;

namespace Romd.Host.Endpoints;

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/libraries")
            .WithTags("Libraries")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("", GetAll)
            .WithName("ListLibraries")
            .Produces<IEnumerable<LibraryDto>>();

        group.MapGet("{libraryId}", GetById)
            .WithName("GetLibraryById")
            .Produces<LibraryDto>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("", Create)
            .WithName("CreateLibrary")
            .Produces<LibraryDto>(StatusCodes.Status201Created)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);

        group.MapPut("{libraryId}", Update)
            .WithName("UpdateLibrary")
            .Produces<LibraryDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);

        group.MapDelete("{libraryId}", Delete)
            .WithName("DeleteLibrary")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapPost("{libraryId}/materialize", ForceMaterialize)
            .WithName("ForceMaterializeLibrary")
            .Produces<JobReference>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("{libraryId}/facets/systems", GetPlatformFacets)
            .WithName("GetLibraryPlatformFacets")
            .Produces<IEnumerable<LibraryFacetDto>>();

        group.MapGet("{libraryId}/facets/genres", GetGenreFacets)
            .WithName("GetLibraryGenreFacets")
            .Produces<IEnumerable<LibraryFacetDto>>();

        group.MapGet("{libraryId}/collections", GetCollections)
            .WithName("GetLibraryCollections")
            .Produces<IEnumerable<LibraryCollectionDto>>();

        group.MapGet("{libraryId}/titles/{titleId}/releases", GetTitleReleaseDiagnostics)
            .WithName("GetLibraryTitleReleaseDiagnostics")
            .Produces<IEnumerable<LibraryTitleReleaseDiagnosticsDto>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("{libraryId}/attachments", GetAttachments)
            .WithName("GetLibraryAttachments").Produces<IReadOnlyList<LibraryAttachmentDto>>();
        group.MapPut("{libraryId}/attachments", SetAttachments)
            .WithName("SetLibraryAttachments").Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);
        group.MapGet("{libraryId}/preview", GetPreview)
            .WithName("GetLibraryPreview").Produces<LibraryPreviewDto>();
        group.MapGet("collection-placements/{collectionId}", GetCollectionPlacements)
            .WithName("GetCollectionLibraryPlacements").Produces<IReadOnlyList<CollectionLibraryPlacementDto>>();
        group.MapPost("{libraryId}/evaluate", Evaluate)
            .WithName("EvaluateLibrary").Produces<LibraryEvaluationDto>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<IResult> Evaluate([FromServices] IReferenceCatalogService referenceCatalog, Sqid libraryId, LibraryEvaluationRequest request,
        IQueryHandler<EvaluateLibraryQuery, LibraryEvaluationDto> handler, CancellationToken ct)
    {
        if (request.Configuration is null) return Results.BadRequest();
        int afterId = request.Cursor is null ? 0 : DecodeIds([request.Cursor], "cursor")[0];
        var result = await handler.HandleAsync(new EvaluateLibraryQuery(libraryId,
            await MapConfigFromDto(request.Configuration, referenceCatalog, ct), request.View, request.Search, afterId), ct);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetCollectionPlacements(Sqid collectionId,
        IQueryHandler<GetCollectionPlacementsQuery, IReadOnlyList<CollectionLibraryPlacementDto>> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetCollectionPlacementsQuery(collectionId), ct);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetAttachments(Sqid libraryId,
        IQueryHandler<GetLibraryAttachmentsQuery, IReadOnlyList<LibraryAttachmentDto>> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetLibraryAttachmentsQuery(libraryId), ct);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> SetAttachments(Sqid libraryId, SetLibraryAttachmentsRequest request,
        ICommandHandler<SetLibraryAttachmentsCommand, Success> handler, CancellationToken ct)
    {
        if (request.Collections is null || request.Collections.Any(c => c is null)) return Results.BadRequest();
        var ids = DecodeIds(request.Collections.Select(c => c.CollectionId).ToList(), "collections");
        var result = await handler.HandleAsync(new SetLibraryAttachmentsCommand(libraryId,
            ids.Select((id, index) => (id, request.Collections[index].IsFeatured)).ToList()), ct);
        return result.Match(_ => Results.NoContent(), ToLibraryProblem);
    }

    private static async Task<IResult> GetPreview(Sqid libraryId,
        IQueryHandler<GetLibraryPreviewQuery, LibraryPreviewDto> handler,
        string? collectionId = null, string? cursor = null, CancellationToken ct = default)
    {
        int? collection = collectionId is null ? null : DecodeIds([collectionId], "collectionId")[0];
        int after = 0;
        int afterOrder = int.MinValue;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out afterOrder))
                return Results.BadRequest();
            after = DecodeIds([parts[1]], "cursor")[0];
        }
        var result = await handler.HandleAsync(new GetLibraryPreviewQuery(libraryId, collection, after, afterOrder), ct);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetAll(IQueryHandler<ListLibrariesQuery, IReadOnlyList<LibraryDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListLibrariesQuery(), cancellationToken);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> GetById(Sqid libraryId,
        IQueryHandler<GetLibraryByIdQuery, LibraryDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetLibraryByIdQuery(libraryId), cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> Create([FromServices] IReferenceCatalogService referenceCatalog,
        [FromBody] CreateLibraryRequest request,
        ICommandHandler<CreateLibraryCommand, LibraryDto> handler,
        CancellationToken cancellationToken)
    {
        var configuration = request.Configuration is not null
            ? await MapConfigFromDto(request.Configuration, referenceCatalog, cancellationToken)
            : new LibraryConfiguration();
        var result = await handler.HandleAsync(
            new CreateLibraryCommand(request.Name, configuration, request.IsDefault),
            cancellationToken);
        return result.Match(
            created => Results.Created($"/api/libraries/{created.Id}", created),
            ToLibraryProblem);
    }

    private static async Task<IResult> Update([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid libraryId,
        [FromBody] UpdateLibraryRequest request,
        ICommandHandler<UpdateLibraryCommand, LibraryDto> handler,
        CancellationToken cancellationToken)
    {
        var configuration = request.Configuration is null
            ? null
            : await MapConfigFromDto(request.Configuration, referenceCatalog, cancellationToken);
        var result = await handler.HandleAsync(
            new UpdateLibraryCommand(libraryId, request.Name, configuration, request.IsDefault),
            cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> Delete(Sqid libraryId,
        ICommandHandler<DeleteLibraryCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteLibraryCommand(libraryId), cancellationToken);
        return result.Match(_ => Results.NoContent(), ToLibraryProblem);
    }

    private static async Task<IResult> ForceMaterialize(Sqid libraryId,
        ICommandHandler<ForceMaterializeLibraryCommand, Guid> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ForceMaterializeLibraryCommand(libraryId),
            cancellationToken);
        return result.Match(
            jobId => Results.Accepted($"/api/jobs/{jobId}", new JobReference(jobId)),
            ToLibraryProblem);
    }

    private static async Task<IResult> GetPlatformFacets(Sqid libraryId,
        IQueryHandler<GetLibraryPlatformFacetsQuery, IReadOnlyList<LibraryFacetDto>> handler,
        int minimumItems = 1,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new GetLibraryPlatformFacetsQuery(libraryId, minimumItems),
            cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetGenreFacets(Sqid libraryId,
        IQueryHandler<GetLibraryGenreFacetsQuery, IReadOnlyList<LibraryFacetDto>> handler,
        int minimumItems = 1,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new GetLibraryGenreFacetsQuery(libraryId, minimumItems),
            cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetCollections(Sqid libraryId,
        IQueryHandler<GetLibraryCollectionsQuery, IReadOnlyList<LibraryCollectionDto>> handler,
        int minimumItems = 1,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new GetLibraryCollectionsQuery(libraryId, minimumItems),
            cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetTitleReleaseDiagnostics(Sqid libraryId,
        Sqid titleId,
        IQueryHandler<
            GetLibraryTitleReleaseDiagnosticsQuery,
            IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>> handler,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new GetLibraryTitleReleaseDiagnosticsQuery(libraryId, titleId),
            cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<LibraryConfiguration> MapConfigFromDto(LibraryConfigurationDto dto, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct)
    {
        var policy = dto.ContentRatingPolicy is not null
            ? MapPolicyFromDto(dto.ContentRatingPolicy)
            : new ContentRatingPolicy();

        var systemIds = new List<int>();
        foreach (var key in dto.AllowedSystemKeys) systemIds.Add(await referenceCatalog.RequireSystemAsync(key, ct));
        return new LibraryConfiguration
        {
            TitleSelectionMode = dto.TitleSelectionMode.ToDomain(),
            AllowedPlatformIds = systemIds,
            ExcludedDatIds = DecodeIds(dto.ExcludedDatIds, "ExcludedDatIds"),
            ContentRatingPolicy = policy,
            AllowedGenres = dto.AllowedGenres.ToList(),
            UnknownGenrePolicy = dto.UnknownGenrePolicy.ToDomain(),
            ShowMissingGames = dto.ShowMissingGames,
            IncludeTitleIds = DecodeIds(dto.IncludeTitleIds, "IncludeTitleIds"),
            ExcludeTitleIds = DecodeIds(dto.ExcludeTitleIds, "ExcludeTitleIds")
        };
    }

    private static ContentRatingPolicy MapPolicyFromDto(ContentRatingPolicyDto dto) =>
        new()
        {
            BasisSelection = dto.BasisSelection.ToDomain(),
            BoardPreference = dto.BoardPreference.Count > 0
                ? dto.BoardPreference.Select(board =>
                    LibraryConfigurationContractMapping.ToDomain(board)).ToList()
                : RatingBoardCatalog.DefaultBoardPreference,
            MaxMinimumAge = dto.MaxMinimumAge,
            AllowRefusedClassification = dto.AllowRefusedClassification,
            UnknownRatingPolicy = dto.UnknownRatingPolicy.ToDomain()
        };

    private static IResult ToLibraryProblem(IReadOnlyList<Error> errors)
    {
        var first = errors.FirstOrDefault();
        return first.Type switch
        {
            ErrorType.NotFound => Results.NotFound(),
            ErrorType.Validation when first.Code == "Libraries.NameRequired" =>
                ProblemResults.ValidationProblem(first.Code, "name", first.Description),
            ErrorType.Validation when first.Code == "Libraries.InvalidConfiguration" =>
                ProblemResults.ValidationProblem(first.Code, "configuration", first.Description),
            _ => errors.ToProblem()
        };
    }

    /// <summary>
    ///     Decodes a list of Sqid strings to integer IDs. Throws <see cref="BadHttpRequestException"/>
    ///     if any ID is malformed, so callers get a 400 instead of silent data loss.
    /// </summary>
    private static List<int> DecodeIds(IReadOnlyList<string> sqids, string fieldName)
    {
        var result = new List<int>(sqids.Count);
        foreach (var sqid in sqids)
        {
            if (!IdCoder.TryDecode(sqid, out var id))
            {
                throw new BadHttpRequestException($"Invalid ID '{sqid}' in {fieldName}");
            }

            result.Add(id);
        }

        return result;
    }
}
