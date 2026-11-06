using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSourceLifecycle;
using System.ComponentModel.DataAnnotations;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog.Commands.SetSourceStatus;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyReviewedDatReplacement;
using Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;
using Romd.Admin.Application.Source.Dat.Queries.GetAllDats;
using Romd.Admin.Application.Source.Dat.Queries.GetDatById;
using Romd.Admin.Application.Source.Dat.Queries.GetUnroutedDats;
using Romd.Admin.Application.Source.Game.Queries.GetGameById;
using Romd.Admin.Application.Source.Game.Queries.GetGamesByDat;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Validation;
using Romd.Contracts.Management.Commands;
using Romd.Domain.Catalog;
using Romd.Host.Authorization;
using CommonModels = Romd.Contracts.Common.Models;
using DomainDatGame = Romd.Domain.Source.Dat.DatGame;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for DAT file operations.
/// </summary>
public static class DatEndpoints
{
    /// <summary>
    ///     Maps all DAT-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapDatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dats")
            .WithTags("Dats");

        group.MapGet("{datId}/source-entries", async (Sqid datId, string? cursor, [Range(1, ISourceEntryReader.MaxLimit)] int? limit, string? query, Sqid? titleId, ISourceEntryReader reader, CancellationToken ct) =>
        {
            var result = await reader.ReadAsync(datId.Value, cursor, limit ?? 50, query, titleId?.Value, ct);
            return result.Match(page => Results.Ok(new CommonModels.Page<Models.SourceEntryReference> { Items = page.Items.Select(e => new Models.SourceEntryReference(IdCoder.Encode(e.GameId), IdCoder.Encode(e.EntryId), e.Name, e.TitleId is int id ? IdCoder.Encode(id) : null, e.TitleName, e.HasLocalPayload, e.ActiveSources)).ToArray(), NextCursor = page.NextCursor, HasNextPage = page.HasNextPage }), errors => errors.ToProblem());
        }).WithName("ListSourceEntries").Produces<CommonModels.Page<Models.SourceEntryReference>>().ProducesProblems(400, 404).RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}/source-impact", async (Sqid datId, string action, IDatSourceManagement service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(datId.Value, action, ct);
            return result.Match(v => Results.Ok(new Models.SourceRemovalImpact(v.Name, v.Action, v.ReviewToken, v.Versions,
                v.StillCovered, v.OwnedWithoutDefinition, v.PersonalWithoutDefinition, v.CatalogOnlyWithoutDefinition, v.Busy)), errors => errors.ToProblem());
        }).WithName("PreviewSourceLifecycle").Produces<Models.SourceRemovalImpact>().ProducesProblem(400).ProducesProblem(404)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{datId}/source-lifecycle", async (Sqid datId, Models.ApplySourceLifecycle request, ICommandHandler<ApplyDatSourceLifecycleCommand, Success> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(datId.Value, request.Action, request.ReviewToken), ct);
            return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
        }).WithName("ApplySourceLifecycle").Produces(204).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("", GetAll)
            .WithName("ListDats")
            .Produces<CommonModels.Page<Models.Dat>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}", GetById)
            .WithName("GetDat")
            .Produces<Models.Dat>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}/download", Download)
            .WithName("DownloadDat")
            .WithDescription("Download the original source DAT file")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        // Nested games endpoints
        group.MapGet("{datId}/games", GetGamesByDatId)
            .WithName("ListDatGames")
            .Produces<CommonModels.Page<Models.DatGame>>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}/games/{gameId}", GetGameById)
            .WithName("GetDatGame")
            .Produces<Models.DatGame>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPatch("{datId}/system", UpdateDatSystem)
            .WithName("UpdateDatSystem")
            .Produces<Models.PlatformAssignment>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPatch("{datId}/source-status", SetSourceStatus)
            .WithName("UpdateDatSourceStatus")
            .WithDescription("Transition the DAT's catalog source lifecycle status")
            .Produces<Models.SourceStatus>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{datId}/replacements/preview", PreviewReplacement)
            .WithName("PreviewDatReplacement")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.DatReplacementPreview>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{datId}/replacements/changes", ReplacementChanges)
            .WithName("ListDatReplacementChanges")
            .WithDescription(ChangesDescription + " The request resends the reviewed candidate file. " + SnapshotBindingDescription)
            .Produces<CommonModels.Page<Models.DatEntryChange>>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery().RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{datId}/replacements/changes/{entryId}", ReplacementEntryChange)
            .WithName("GetDatReplacementChange")
            .WithDescription(EntryChangeDescription + " The request resends the reviewed candidate file. " + SnapshotBindingDescription)
            .Produces<Models.DatEntryChangeDetail>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery().RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{datId}/replacements", ApplyReviewedReplacement)
            .WithName("CreateDatReplacement")
            .WithDescription("Start replacement of the active DAT with the reviewed candidate. " + SnapshotBindingDescription)
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<IResult> GetAll([FromServices] IReferenceCatalogService referenceCatalog,
        IDatRepository repository, SystemKeyParameter? systemKey, bool? unrouted, string? query, string? cursor,
        CancellationToken cancellationToken, [Range(1, 100)] int limit = 50)
    {
        var result = await repository.GetPageAsync(systemKey?.SystemId, unrouted, query, cursor, limit, cancellationToken);
        if (result.IsError)
            return result.Errors.ToProblem();
        var keys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        return Results.Ok(new CommonModels.Page<Models.Dat> { Items = result.Value.Items.ToContract(keys).ToArray(), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage });
    }

    private static async Task<IResult> GetById([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid datId,
        IQueryHandler<GetDatByIdQuery, DatWithSize> handler,
        CancellationToken cancellationToken = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var result = await handler.HandleAsync(new GetDatByIdQuery(datId), cancellationToken);
        return result.Match(
            dat => Results.Ok(dat.ToContract(systemKeys)),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> Download(Sqid datId,
        IDatRepository datRepository,
        IFileStorageService fileStorage,
        CancellationToken cancellationToken)
    {
        var dat = await datRepository.GetByIdAsync(datId, cancellationToken);
        if (dat is null)
        {
            return new[] { Error.NotFound("Dats.NotFound", "DAT source file not found.") }.ToProblem();
        }

        var stream = await fileStorage.RetrieveByIdAsync(dat.FileId, cancellationToken);
        if (stream is null)
        {
            return new[] { Error.NotFound("Dats.NotFound", "DAT source file not found.") }.ToProblem();
        }

        return Results.File(
            stream,
            "application/octet-stream",
            dat.OriginalFilename);
    }

    private static async Task<IResult> GetGamesByDatId(Sqid datId,
        IQueryHandler<GetGamesByDatQuery, PagedList<DomainDatGame>> handler,
        string? cursor = null,
        [Range(1, GetGamesByDatQuery.MaxLimit)] int limit = 50,
        BiosFilter bios = BiosFilter.Exclude,
        CancellationToken cancellationToken = default)
    {
        var query = new GetGamesByDatQuery { DatId = datId, Cursor = cursor, Limit = limit, BiosFilter = bios };

        var result = await handler.HandleAsync(query, cancellationToken);
        return result.Match(
            pagedGames => Results.Ok(pagedGames.ToContract()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetGameById(Sqid datId,
        Sqid gameId,
        IQueryHandler<GetGameByIdQuery, DomainDatGame> handler,
        CancellationToken cancellationToken = default)
    {
        var query = new GetGameByIdQuery { DatId = datId, GameId = gameId };

        var result = await handler.HandleAsync(query, cancellationToken);
        return result.Match(
            game => Results.Ok(game.ToContract()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> UpdateDatSystem([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid datId,
        AssignPlatform request,
        ICommandHandler<AssignPlatformCommand, PlatformAssignmentResult> handler,
        CancellationToken cancellationToken)
    {
        var (systemKeyProblem, platformId) = await referenceCatalog.ResolveBodySystemKeyAsync(request.SystemKey, "systemKey", cancellationToken);
        if (systemKeyProblem is not null)
            return systemKeyProblem;

        var commandResult = AssignPlatformCommand.Create(datId, platformId);
        if (commandResult.IsError)
        {
            return commandResult.Errors.ToProblem();
        }

        var result = await handler.HandleAsync(commandResult.Value, cancellationToken);

        return result.Match(
            assignmentResult => Results.Ok(new Models.PlatformAssignment
            {
                GamesUpdated = assignmentResult.GamesUpdated,
                NewTitlesCreated = assignmentResult.NewTitlesCreated,
                ExistingTitlesMatched = assignmentResult.ExistingTitlesMatched
            }),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> SetSourceStatus(Sqid datId,
        SetSourceStatus request,
        ICommandHandler<SetSourceStatusCommand, SourceStatusResult> handler,
        CancellationToken cancellationToken)
    {
        var commandResult = SetSourceStatusCommand.Create(datId, request.Status.ToString());
        if (commandResult.IsError)
        {
            return commandResult.Errors.ToProblem();
        }

        var result = await handler.HandleAsync(commandResult.Value, cancellationToken);

        return result.Match(
            statusResult => Results.Ok(new Models.SourceStatus
            {
                Status = Enum.Parse<Models.SourceLifecycleStatus>(statusResult.Status.ToString()),
                Changed = statusResult.Changed
            }),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> PreviewReplacement(Sqid datId, [FromForm] IFormFile? file,
        IDatReplacementReview review, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return ProblemResults.ValidationProblem("DatReview.FileRequired", "file", "Choose a non-empty DAT document.");
        await using var stream = file.OpenReadStream();
        var result = await review.PreviewAsync(datId.Value, stream, ct);
        return result.Match(p => Results.Ok(ReviewContract(p)), errors => errors.ToProblem());
    }

    private static async Task<IResult> ReplacementChanges(Sqid datId, [FromForm] IFormFile? file,
        [FromForm, RegularExpression(DatReviewPins.ActivePattern)] string activeSha256, [FromForm, RegularExpression(DatReviewPins.CandidatePattern), Length(DatReviewPins.HashLength, DatReviewPins.HashLength)] string candidateSha256, [FromForm, RegularExpression(DatChangePageRequest.ChangePattern)] string? change,
        [FromForm, MaxLength(DatChangePageRequest.MaxQueryLength)] string? query, [FromForm] string? cursor, [FromForm, Range(1, DatChangePageRequest.MaxLimit)] int? limit,
        IDatReplacementReview review, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return ProblemResults.ValidationProblem("DatReview.FileRequired", "file", "Choose a non-empty DAT document.");
        var page = DatChangePageRequest.ForEntries(activeSha256, candidateSha256, change, query, cursor, limit);
        if (page.IsError)
            return page.Errors.ToProblem(InvalidChangeRequest);
        await using var stream = file.OpenReadStream();
        var result = await review.ListChangesAsync(datId.Value, "", stream, page.Value, ct);
        return result.Match(value => Results.Ok(ChangesContract(value)), errors => errors.ToProblem());
    }

    private static async Task<IResult> ReplacementEntryChange(Sqid datId, string entryId, [FromForm] IFormFile? file,
        [FromForm, RegularExpression(DatReviewPins.ActivePattern)] string activeSha256, [FromForm, RegularExpression(DatReviewPins.CandidatePattern), Length(DatReviewPins.HashLength, DatReviewPins.HashLength)] string candidateSha256, [FromForm] string? cursor,
        [FromForm, Range(1, DatChangePageRequest.MaxLimit)] int? limit, IDatReplacementReview review, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return ProblemResults.ValidationProblem("DatReview.FileRequired", "file", "Choose a non-empty DAT document.");
        var page = EntryPage(entryId, activeSha256, candidateSha256, cursor, limit);
        if (page.IsError)
            return page.Errors.ToProblem(InvalidChangeRequest);
        await using var stream = file.OpenReadStream();
        var result = await review.GetEntryChangeAsync(datId.Value, "", stream, page.Value.EntryName, page.Value.Page, ct);
        return result.Match(value => Results.Ok(EntryChangeContract(value)), errors => errors.ToProblem());
    }

    internal const string ChangesDescription = "A keyset page of the review's changed entries in ordinal name order, " +
        "filtered by change kind and a case-insensitive name query. The cursor is bound to the reviewed pair and filters.";
    internal const string EntryChangeDescription = "One changed entry of the review, with a keyset page of its changed " +
        "files. entryId is the opaque id from the change list; an unknown or unchanged entry is a 404.";
    internal const string SnapshotBindingDescription = "activeSha256 and candidateSha256 are a snapshot binding of the " +
        "reviewed document pair from the preview, not a precondition: a stale pair is a 409. activeSha256 is the empty " +
        "string when no document is installed yet.";

    /// <summary>
    ///     Validates an entry page request in one stage: the entry id and every page input, each keyed by its wire name.
    /// </summary>
    internal static ErrorOr<(string EntryName, DatChangePageRequest Page)> EntryPage(string entryId,
        string? activeSha256, string? candidateSha256, string? cursor, int? limit)
    {
        bool valid = DatEntryId.TryDecode(entryId, out string entryName);
        var page = DatChangePageRequest.ForFiles(activeSha256, candidateSha256, entryName, cursor, limit);
        List<Error> errors = valid ? [] : [FieldErrors.Validation("DatReview.InvalidEntryId", "Use an entry id from the change list.", "entryId")];
        if (page.IsError)
            errors.AddRange(page.Errors);
        return errors.Count > 0 ? errors : (entryName, page.Value);
    }

    /// <summary>The errorCode of a change request's failed validation stage, whose failures name their inputs.</summary>
    internal const string InvalidChangeRequest = "DatReview.InvalidRequest";

    internal static CommonModels.Page<Models.DatEntryChange> ChangesContract(PagedList<DatEntryChange> page) => new()
    {
        Items = page.Items.Select(EntryContract).ToArray(),
        NextCursor = page.NextCursor,
        HasNextPage = page.HasNextPage
    };

    internal static Models.DatEntryChangeDetail EntryChangeContract(DatEntryChangeDetail value) => new(
        DatEntryId.Encode(value.Name), value.Name, value.Change, value.Fields.Select(Field).ToArray(),
        new CommonModels.Page<Models.DatFileChange>
        {
            Items = value.Files.Items.Select(x => new Models.DatFileChange(x.Name, x.Kind, x.Change, x.ChecksumsChanged, x.Fields.Select(Field).ToArray())).ToArray(),
            NextCursor = value.Files.NextCursor,
            HasNextPage = value.Files.HasNextPage
        }, value.ActiveSha256, value.CandidateSha256);

    private static Models.DatEntryChange EntryContract(DatEntryChange x) =>
        new(DatEntryId.Encode(x.Name), x.Name, x.Change, x.FilesAdded, x.FilesRemoved, x.FilesChanged);
    private static Models.DatFieldChange Field(DatFieldChange value) => new(value.Field, value.Before, value.After);

    private static async Task<IResult> ApplyReviewedReplacement(Sqid datId, [FromForm] IFormFile? file,
        [FromForm, RegularExpression(DatReviewPins.ActivePattern)] string activeSha256, [FromForm, RegularExpression(DatReviewPins.CandidatePattern), Length(DatReviewPins.HashLength, DatReviewPins.HashLength)] string candidateSha256,
        ICommandHandler<ApplyReviewedDatReplacementCommand, Guid> handler, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return ProblemResults.ValidationProblem("DatReview.FileRequired", "file", "Choose a non-empty DAT document.");
        await using var stream = file.OpenReadStream();
        var result = await handler.HandleAsync(new(datId.Value, stream, activeSha256, candidateSha256), ct);
        return result.ToAccepted();
    }


    internal static Models.DatReplacementPreview ReviewContract(DatReplacementPreview p) => new(
        p.ActiveSha256, p.CandidateSha256, p.ActiveVersion, p.CandidateVersion, p.Unchanged,
        p.ActiveEntries, p.CandidateEntries, p.EntriesAdded, p.EntriesRemoved, p.EntriesChanged,
        p.FilesAdded, p.FilesRemoved, p.FilesChanged, p.HashesChanged, p.ActiveBiosEntries,
        p.CandidateBiosEntries, p.Changes.Select(EntryContract).ToArray(), p.Truncated, p.ActiveFiles, p.CandidateFiles);
}
