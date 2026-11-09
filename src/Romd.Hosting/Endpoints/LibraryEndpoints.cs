using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.Libraries.Queries.GetLibraryDiscSetOptions;
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

using ContractContentRatingPolicy = Romd.Contracts.Management.Libraries.ContentRatingPolicy;
using ContractLibrary = Romd.Contracts.Management.Libraries.Library;
using ContractLibraryConfiguration = Romd.Contracts.Management.Libraries.LibraryConfiguration;
using ContractLibraryTitleReleaseDiagnostics = Romd.Contracts.Management.Libraries.LibraryTitleReleaseDiagnostics;

using ContentRatingPolicy = Romd.Domain.Libraries.ContentRatingPolicy;
using LibraryConfiguration = Romd.Domain.Libraries.LibraryConfiguration;

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
            .Produces<Page<ContractLibrary>>().ProducesProblems(400);

        group.MapGet("{libraryId}", GetById)
            .WithName("GetLibrary")
            .Produces<ContractLibrary>()
            .ProducesProblems(StatusCodes.Status404NotFound);

        group.MapGet("{libraryId}/disc-set-options", async (Sqid libraryId, string? query, string? cursor,
            [Range(1, 100)] int? limit, IQueryHandler<GetLibraryDiscSetOptionsQuery, LibraryDiscSetOptions> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(libraryId, cursor, limit ?? 100, query), ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("GetLibraryDiscSetOptions").Produces<LibraryDiscSetOptions>().ProducesProblems(400, 404);

        group.MapPost("", Create)
            .WithName("CreateLibrary")
            .ProducesCreated<ContractLibrary>()
            .ProducesProblems(StatusCodes.Status400BadRequest);

        group.MapPatch("{libraryId}", Update)
            .WithName("UpdateLibrary")
            .Produces<ContractLibrary>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .ProducesProblems(StatusCodes.Status400BadRequest);

        group.MapDelete("{libraryId}", Delete)
            .WithName("DeleteLibrary")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .ProducesProblems(StatusCodes.Status409Conflict);

        group.MapPost("{libraryId}/materialize", ForceMaterialize)
            .WithName("MaterializeLibrary")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status404NotFound);

        group.MapGet("{libraryId}/facets/systems", GetPlatformFacets)
            .WithName("ListLibrarySystemFacets")
            .Produces<IEnumerable<LibraryFacet>>().ProducesProblems(404).WithDescription("At most one facet per configured reference system.");

        group.MapGet("{libraryId}/facets/genres", GetGenreFacets)
            .WithName("ListLibraryGenreFacets")
            .Produces<Page<LibraryFacet>>().ProducesProblems(400, 404);

        group.MapGet("{libraryId}/collections", GetCollections)
            .WithName("ListLibraryCollections")
            .Produces<Page<LibraryCollection>>().ProducesProblems(400, 404);

        group.MapGet("{libraryId}/titles/{titleId}/releases", GetTitleReleaseDiagnostics)
            .WithName("ListLibraryTitleReleaseDiagnostics")
            .Produces<Page<ContractLibraryTitleReleaseDiagnostics>>().ProducesProblems(400)
            .ProducesProblems(StatusCodes.Status404NotFound);

        group.MapGet("{libraryId}/attachments", GetAttachments)
            .WithName("ListLibraryAttachments").Produces<IReadOnlyList<LibraryAttachment>>().ProducesProblems(404).WithDescription("At most 500 attachments; the replacement write enforces uniqueness and this bound.");
        group.MapPut("{libraryId}/attachments", SetAttachments)
            .WithName("ReplaceLibraryAttachments").Produces(StatusCodes.Status204NoContent).ProducesProblems(404)
            .ProducesProblems(StatusCodes.Status400BadRequest);
        group.MapGet("{libraryId}/preview", GetPreview)
            .WithName("ListLibraryPreviewTitles").Produces<Page<LibraryPreviewTitle>>().ProducesProblems(400, 404);
        app.MapGet("/collections/{collectionId}/library-placements", GetCollectionPlacements).RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .WithName("ListCollectionLibraryPlacements").Produces<Page<CollectionLibraryPlacement>>().ProducesProblems(400, 404);
        group.MapPost("{libraryId}/configuration/preview", Evaluate)
            .WithName("PreviewLibraryConfiguration").Produces<LibraryEvaluation>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .ProducesProblems(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<IResult> Evaluate([FromServices] IReferenceCatalogService referenceCatalog, Sqid libraryId, LibraryEvaluationRequest request,
        IQueryHandler<EvaluateLibraryQuery, LibraryEvaluation> handler, CancellationToken ct)
    {
        if (request.Configuration is null)
            return ProblemResults.ValidationProblem("Libraries.InvalidConfiguration", "configuration", "Configuration is required.");
        var failures = new Dictionary<string, string[]>();
        var configuration = await MapConfigFromDto(request.Configuration, referenceCatalog, failures, ct);
        if (configuration is null || failures.Count > 0)
            return InvalidConfiguration(failures);
        var result = await handler.HandleAsync(new(libraryId.Value, configuration, request.View, request.Query, 0, request.Cursor, request.Limit), ct);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetCollectionPlacements(Sqid collectionId, string? cursor,
        IQueryHandler<GetCollectionPlacementsQuery, Page<CollectionLibraryPlacement>> handler, CancellationToken ct, [Range(1, 100)] int limit = 50) =>
        (await handler.HandleAsync(new(collectionId.Value, cursor, limit), ct)).Match(Results.Ok, ToLibraryProblem);

    private static async Task<IResult> GetAttachments(Sqid libraryId,
        IQueryHandler<GetLibraryAttachmentsQuery, IReadOnlyList<LibraryAttachment>> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetLibraryAttachmentsQuery(libraryId), ct);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> SetAttachments(Sqid libraryId, SetLibraryAttachmentsRequest request,
        ICommandHandler<SetLibraryAttachmentsCommand, Success> handler, CancellationToken ct)
    {
        if (request.Collections is null || request.Collections.Any(c => c is null))
            return ProblemResults.ValidationProblem("Libraries.InvalidAttachments", "collections", "Choose a collection array with no null members.");
        if (RequestBodyIds.DecodeAll(request.Collections.Select(c => c.CollectionId), "collections", InvalidIdCode,
                out IReadOnlyList<int> ids) is { } problem)
            return problem;
        var result = await handler.HandleAsync(new SetLibraryAttachmentsCommand(libraryId,
            ids.Select((id, index) => (id, request.Collections[index].IsFeatured)).ToList()), ct);
        return result.Match(_ => Results.NoContent(), ToLibraryProblem);
    }

    private static async Task<IResult> GetPreview(Sqid libraryId, IQueryHandler<GetLibraryPreviewQuery, Page<LibraryPreviewTitle>> handler,
        Sqid? collectionId = null, string? cursor = null, [Range(1, 100)] int limit = 48, CancellationToken ct = default) =>
        (await handler.HandleAsync(new(libraryId.Value, collectionId?.Value, cursor, limit), ct)).Match(Results.Ok, ToLibraryProblem);

    private static async Task<IResult> GetAll(IQueryHandler<ListLibrariesQuery, Page<ContractLibrary>> handler,
        string? query, string? cursor, CancellationToken cancellationToken, [Range(1, 100)] int limit = 50)
    {
        var result = await handler.HandleAsync(new ListLibrariesQuery(query, cursor, limit), cancellationToken);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> GetById(Sqid libraryId,
        IQueryHandler<GetLibraryByIdQuery, ContractLibrary> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetLibraryByIdQuery(libraryId), cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> Create([FromServices] IReferenceCatalogService referenceCatalog,
        [FromBody] CreateLibraryRequest request,
        ICommandHandler<CreateLibraryCommand, ContractLibrary> handler,
        CancellationToken cancellationToken)
    {
        var failures = new Dictionary<string, string[]>();
        CollectName(request.Name, failures);
        var configuration = request.Configuration is not null
            ? await MapConfigFromDto(request.Configuration, referenceCatalog, failures, cancellationToken)
            : new LibraryConfiguration();
        if (configuration is null)
            return InvalidConfiguration(failures);
        if (failures.Count > 0)
            return NameRequired(failures);
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
        ICommandHandler<UpdateLibraryCommand, ContractLibrary> handler,
        CancellationToken cancellationToken)
    {
        var failures = new Dictionary<string, string[]>();
        CollectName(request.Name, failures);
        LibraryConfiguration? configuration = null;
        if (request.Configuration is not null)
        {
            configuration = await MapConfigFromDto(request.Configuration, referenceCatalog, failures, cancellationToken);
            if (configuration is null)
                return InvalidConfiguration(failures);
        }

        if (failures.Count > 0)
            return NameRequired(failures);

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
            JobAccepted.Result,
            ToLibraryProblem);
    }

    private static async Task<IResult> GetPlatformFacets(Sqid libraryId,
        IQueryHandler<GetLibraryPlatformFacetsQuery, IReadOnlyList<LibraryFacet>> handler,
        int minimumItems = 1,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(
            new GetLibraryPlatformFacetsQuery(libraryId, minimumItems),
            cancellationToken);
        return result.Match(Results.Ok, ToLibraryProblem);
    }

    private static async Task<IResult> GetGenreFacets(Sqid libraryId, IQueryHandler<GetLibraryGenreFacetsQuery, Page<LibraryFacet>> handler, string? cursor,
        CancellationToken ct, [Range(0, int.MaxValue)] int minimumItems = 1, [Range(1, 100)] int limit = 50) =>
        (await handler.HandleAsync(new(libraryId.Value, minimumItems, cursor, limit), ct)).Match(Results.Ok, ToLibraryProblem);

    private static async Task<IResult> GetCollections(Sqid libraryId, IQueryHandler<GetLibraryCollectionsQuery, Page<LibraryCollection>> handler, string? cursor,
        CancellationToken ct, [Range(0, int.MaxValue)] int minimumItems = 1, [Range(1, 100)] int limit = 50) =>
        (await handler.HandleAsync(new(libraryId.Value, minimumItems, cursor, limit), ct)).Match(Results.Ok, ToLibraryProblem);

    private static async Task<IResult> GetTitleReleaseDiagnostics(Sqid libraryId, Sqid titleId, IQueryHandler<GetLibraryTitleReleaseDiagnosticsQuery, Page<ContractLibraryTitleReleaseDiagnostics>> handler, string? cursor,
        CancellationToken ct, [Range(1, 100)] int limit = 50) =>
        (await handler.HandleAsync(new(libraryId.Value, titleId.Value, cursor, limit), ct)).Match(Results.Ok, ToLibraryProblem);

    /// <summary>
    ///     Maps a configuration body, recording every invalid id or system key by field in the
    ///     request's <paramref name="failures" />, so one envelope reports the configuration's failures
    ///     with the request's others. The configuration is <c>null</c> exactly when one of its fields
    ///     failed.
    /// </summary>
    private static async Task<LibraryConfiguration?> MapConfigFromDto(ContractLibraryConfiguration dto,
        [FromServices] IReferenceCatalogService referenceCatalog, IDictionary<string, string[]> failures, CancellationToken ct)
    {
        int priorFailures = failures.Count;
        var excludedDatIds = RequestBodyIds.Collect(dto.ExcludedDatIds, "configuration.excludedDatIds", failures);
        var selectedDiscSetIds = RequestBodyIds.Collect(dto.SelectedDiscSetIds, "configuration.selectedDiscSetIds", failures);
        var includeTitleIds = RequestBodyIds.Collect(dto.IncludeTitleIds, "configuration.includeTitleIds", failures);
        var excludeTitleIds = RequestBodyIds.Collect(dto.ExcludeTitleIds, "configuration.excludeTitleIds", failures);
        var systemIds = await referenceCatalog.CollectBodySystemKeysAsync(
            dto.AllowedSystemKeys, "configuration.allowedSystemKeys", failures, ct);
        if (failures.Count > priorFailures)
            return null;

        var policy = dto.ContentRatingPolicy is not null
            ? MapPolicyFromDto(dto.ContentRatingPolicy)
            : new ContentRatingPolicy();

        return new LibraryConfiguration
        {
            TitleSelectionMode = dto.TitleSelectionMode.ToDomain(),
            AllowedPlatformIds = systemIds,
            ExcludedDatIds = excludedDatIds,
            ContentRatingPolicy = policy,
            AllowedGenres = dto.AllowedGenres.ToList(),
            UnknownGenrePolicy = dto.UnknownGenrePolicy.ToDomain(),
            IncludeUntrackedTitles = dto.IncludeUntrackedTitles,
            ShowMissingGames = dto.ShowMissingGames,
            SelectedDiscSetIds = selectedDiscSetIds,
            IncludeTitleIds = includeTitleIds,
            ExcludeTitleIds = excludeTitleIds
        };
    }

    private static void CollectName(string? name, IDictionary<string, string[]> failures)
    {
        if (string.IsNullOrWhiteSpace(name))
            failures["name"] = [LibraryErrors.NameRequired().Description];
    }

    // One envelope carries one code: a failed configuration names it, since it is the aggregate
    // validation (docs/decisions/api-contract-policy.md, "One Error Envelope").
    private static IResult InvalidConfiguration(IReadOnlyDictionary<string, string[]> failures) =>
        ProblemResults.ValidationProblem(InvalidConfigurationCode, failures);

    private static IResult NameRequired(IReadOnlyDictionary<string, string[]> failures) =>
        ProblemResults.ValidationProblem(LibraryErrors.NameRequired().Code, failures);

    private static ContentRatingPolicy MapPolicyFromDto(ContractContentRatingPolicy dto) =>
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

    private static IResult ToLibraryProblem(IReadOnlyList<Error> errors) => errors.ToProblem();

    private const string InvalidIdCode = "Libraries.InvalidId";

    private const string InvalidConfigurationCode = "Libraries.InvalidConfiguration";
}
