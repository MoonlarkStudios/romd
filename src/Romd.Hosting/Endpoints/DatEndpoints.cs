using Romd.Application.Common.ReferenceCatalog;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;
using Romd.Admin.Application.Catalog.Commands.SetSourceStatus;
using Romd.Admin.Application.Source.Dat.Queries.GetAllDats;
using Romd.Admin.Application.Source.Dat.Queries.GetDatById;
using Romd.Admin.Application.Source.Dat.Queries.GetUnroutedDats;
using Romd.Admin.Application.Source.Game.Queries.GetGameById;
using Romd.Admin.Application.Source.Game.Queries.GetGamesByDat;
using Romd.Admin.Application.Storage.Files;
using Romd.Contracts.Management.Commands;
using Romd.Domain.Catalog;
using Romd.Host.Authorization;
using DomainDatGame = Romd.Domain.Source.Dat.DatGame;
using CommonModels = Romd.Contracts.Common.Models;
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

        group.MapGet("{datId}/source-entries", async (Sqid datId, Sqid? cursor, int? limit, string? search, Sqid? titleId, ISourceEntryReader reader, CancellationToken ct) =>
        {
            var result = await reader.ReadAsync(datId.Value, cursor?.Value, limit ?? 50, search, titleId?.Value, ct);
            return result.Match(page => Results.Ok(new Models.SourceEntryPage(page.Items.Select(e => new Models.SourceEntryReference(
                IdCoder.Encode(e.GameId), IdCoder.Encode(e.EntryId), e.Name, e.TitleId is int id ? IdCoder.Encode(id) : null,
                e.TitleName, e.HasLocalPayload, e.ActiveSources)).ToList(), page.NextCursor is int next ? IdCoder.Encode(next) : null)), errors => errors.ToProblem());
        }).WithName("ListSourceEntries").Produces<Models.SourceEntryPage>().ProducesProblem(400).ProducesProblem(404)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}/source-impact", async (Sqid datId, string action, IDatSourceManagement service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(datId.Value, action, ct);
            return result.Match(v => Results.Ok(new Models.SourceRemovalImpact(v.Name, v.Action, v.ReviewToken, v.Versions,
                v.StillCovered, v.OwnedWithoutDefinition, v.PersonalWithoutDefinition, v.CatalogOnlyWithoutDefinition, v.Busy)), errors => errors.ToProblem());
        }).WithName("PreviewSourceLifecycle").Produces<Models.SourceRemovalImpact>().ProducesProblem(400).ProducesProblem(404)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{datId}/source-lifecycle", async (Sqid datId, Models.ApplySourceLifecycle request, IDatSourceManagement service, CancellationToken ct) =>
        {
            var result = await service.ApplyAsync(datId.Value, request.Action, request.ReviewToken, ct);
            return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
        }).WithName("ApplySourceLifecycle").Produces(204).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("", GetAll)
            .WithName("ListDats")
            .Produces<IEnumerable<Models.Dat>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("unrouted", GetUnrouted)
            .WithName("ListUnroutedDats")
            .WithDescription("List DATs with no system assigned, with the count of stored ROM files waiting on each")
            .Produces<IEnumerable<Models.UnroutedDat>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}", GetById)
            .WithName("GetDatById")
            .Produces<Models.Dat>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}/download", Download)
            .WithName("DownloadDat")
            .WithDescription("Download the original source DAT file")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        // Nested games endpoints
        group.MapGet("{datId}/games", GetGamesByDatId)
            .WithName("ListGamesByDat")
            .Produces<CommonModels.Page<Models.DatGame>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{datId}/games/{gameId}", GetGameById)
            .WithName("GetGameByDat")
            .Produces<Models.DatGame>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPatch("{datId}/platform", AssignPlatform)
            .WithName("AssignDatPlatform")
            .Produces<Models.PlatformAssignment>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPatch("{datId}/source-status", SetSourceStatus)
            .WithName("SetDatSourceStatus")
            .WithDescription("Transition the DAT's catalog source lifecycle status")
            .Produces<Models.SourceStatus>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPut("{datId}/replace", ReplaceDat)
            .WithName("ReplaceDat")
            .WithDescription("Replace an existing DAT with a new file (async)")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{datId}/replacement-preview", PreviewReplacement)
            .WithName("PreviewDatReplacement")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.DatReplacementPreview>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{datId}/replacement-changes", ReplacementChanges)
            .WithName("GetDatReplacementChanges").Produces<Models.DatChangePage>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery().RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{datId}/subscription/changes", async (Sqid datId, Models.DatChangeRequest request, IDatSubscriptionService service, CancellationToken ct) =>
        {
            var result = await service.ChangesAsync(datId.Value, ChangeQuery(request), ct);
            return result.Match(value => Results.Ok(ChangeContract(value)), errors => errors.ToProblem());
        }).WithName("GetDatSubscriptionChanges").Produces<Models.DatChangePage>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{datId}/reviewed-replacement", ApplyReviewedReplacement)
            .WithName("ApplyReviewedDatReplacement")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("{datId}/subscription", GetSubscription)
            .WithName("GetDatSubscription").Produces<Models.DatSubscriptionStatus>()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{datId}/subscription/check", CheckSubscription)
            .WithName("CheckDatSubscription").Produces<Models.DatSubscriptionStatus>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{datId}/subscription/preview", PreviewSubscription)
            .WithName("PreviewDatSubscription").Produces<Models.DatReplacementPreview>()
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{datId}/subscription/apply", ApplySubscription)
            .WithName("ApplyDatSubscription").Produces<Models.UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<IResult> GetAll([FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetAllDatsQuery, IReadOnlyList<DatWithSize>> handler,
        CancellationToken cancellationToken = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var result = await handler.HandleAsync(new GetAllDatsQuery(), cancellationToken);
        return result.Match(
            dats => Results.Ok(dats.ToContract(systemKeys)),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetUnrouted([FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetUnroutedDatsQuery, IReadOnlyList<UnroutedDatSummary>> handler,
        CancellationToken cancellationToken = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var result = await handler.HandleAsync(new GetUnroutedDatsQuery(), cancellationToken);
        return result.Match(
            summaries => Results.Ok(summaries.ToContract(systemKeys)),
            errors => errors.ToProblem());
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
            _ => Results.NotFound());
    }

    private static async Task<IResult> Download(Sqid datId,
        IDatRepository datRepository,
        IFileStorageService fileStorage,
        CancellationToken cancellationToken)
    {
        var dat = await datRepository.GetByIdAsync(datId, cancellationToken);
        if (dat is null)
        {
            return Results.NotFound();
        }

        var stream = await fileStorage.RetrieveByIdAsync(dat.FileId, cancellationToken);
        if (stream is null)
        {
            return Results.NotFound();
        }

        return Results.File(
            stream,
            "application/octet-stream",
            dat.OriginalFilename);
    }

    private static async Task<IResult> GetGamesByDatId(Sqid datId,
        IQueryHandler<GetGamesByDatQuery, PagedList<DomainDatGame>> handler,
        string? cursor = null,
        int limit = 50,
        BiosFilter bios = BiosFilter.Exclude,
        CancellationToken cancellationToken = default)
    {
        // Clamp limit to reasonable bounds
        limit = Math.Clamp(limit, 1, 100);

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

    private static async Task<IResult> AssignPlatform([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid datId,
        AssignPlatform request,
        ICommandHandler<AssignPlatformCommand, PlatformAssignmentResult> handler,
        CancellationToken cancellationToken)
    {
        var platformId = await referenceCatalog.RequireSystemAsync(request.SystemKey, cancellationToken);

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

    private static async Task<IResult> ReplaceDat([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid datId,
        [FromForm] IFormFile? file,
        [FromQuery] string? systemKey,
        IDatRepository datRepository,
        IReplaceDatJobCreator jobCreator,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return ProblemResults.ValidationProblem(
                "Upload.FileRequired",
                "file",
                "A non-empty file is required.");
        }

        // Verify the DAT exists before creating the job
        var existing = await datRepository.GetByIdAsync(datId, ct);
        if (existing is null)
        {
            return Results.NotFound();
        }

        await using var stream = file.OpenReadStream();
        var result = await jobCreator.CreateAsync(
            datId,
            stream,
            file.FileName,
            (systemKey is null ? (int?)null : await referenceCatalog.RequireSystemAsync(systemKey, ct)),
            ct);

        return Results.Accepted(result.StatusUrl, new Models.UploadAccepted(
            result.JobId,
            result.BackgroundJobId,
            result.StatusUrl));
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
        [FromForm] string activeSha256, [FromForm] string candidateSha256, [FromForm] int offset,
        [FromForm] string? search, [FromForm] string? change, [FromForm] string? entryName,
        IDatReplacementReview review, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return ProblemResults.ValidationProblem("DatReview.FileRequired", "file", "Choose a non-empty DAT document.");
        await using var stream = file.OpenReadStream();
        var result = await review.ChangesAsync(datId.Value, "", stream,
            new(activeSha256, candidateSha256, search, change, offset, entryName), ct);
        return result.Match(value => Results.Ok(ChangeContract(value)), errors => errors.ToProblem());
    }
    internal static DatChangeQuery ChangeQuery(Models.DatChangeRequest q) => new(q.ActiveSha256, q.CandidateSha256, q.Search, q.Change, q.Offset, q.EntryName);
    internal static Models.DatChangePage ChangeContract(DatChangePage page) => new(page.ActiveSha256, page.CandidateSha256,
        page.Offset, page.PageSize, page.Total, page.Entries.Select(x => new Models.DatEntryChange(x.Name, x.Change, x.FilesAdded, x.FilesRemoved, x.FilesChanged)).ToArray(),
        page.EntryName, page.EntryFields.Select(Field).ToArray(), page.Files.Select(x => new Models.DatFileChange(x.Name, x.Kind, x.Change, x.ChecksumsChanged, x.Fields.Select(Field).ToArray())).ToArray());
    private static Models.DatFieldChange Field(DatFieldChange value) => new(value.Field, value.Before, value.After);

    private static async Task<IResult> ApplyReviewedReplacement(Sqid datId, [FromForm] IFormFile? file,
        [FromForm] string activeSha256, [FromForm] string candidateSha256,
        IDatReplacementReview review, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return ProblemResults.ValidationProblem("DatReview.FileRequired", "file", "Choose a non-empty DAT document.");
        await using var stream = file.OpenReadStream();
        var result = await review.ApplyAsync(datId.Value, stream, activeSha256, candidateSha256, ct);
        return result.Match(r => Results.Accepted(r.StatusUrl, new Models.UploadAccepted(
            r.JobId, r.BackgroundJobId, r.StatusUrl)), errors => errors.ToProblem());
    }


    internal static Models.DatReplacementPreview ReviewContract(DatReplacementPreview p) => new(
        p.ActiveSha256, p.CandidateSha256, p.ActiveVersion, p.CandidateVersion, p.Unchanged,
        p.ActiveEntries, p.CandidateEntries, p.EntriesAdded, p.EntriesRemoved, p.EntriesChanged,
        p.FilesAdded, p.FilesRemoved, p.FilesChanged, p.HashesChanged, p.ActiveBiosEntries,
        p.CandidateBiosEntries, p.Changes.Select(c => new Models.DatEntryChange(
            c.Name, c.Change, c.FilesAdded, c.FilesRemoved, c.FilesChanged)).ToArray(), p.Truncated, p.ActiveFiles, p.CandidateFiles);
    private static Models.DatSubscriptionStatus SubscriptionContract(DatSubscriptionStatus s) => new(
        s.Available, s.Subscribed, s.State, s.LastCheckedAt, s.Message, s.CandidateSha256, s.JobId);
    private static async Task<IResult> GetSubscription(Sqid datId, IDatSubscriptionService service, CancellationToken ct)
    {
        var result = await service.GetAsync(datId.Value, ct);
        return result.Match(s => Results.Ok(SubscriptionContract(s)), errors => errors.ToProblem());
    }
    private static async Task<IResult> CheckSubscription(Sqid datId, IDatSubscriptionService service, CancellationToken ct)
    {
        var result = await service.CheckAsync(datId.Value, ct);
        return result.Match(s => Results.Ok(SubscriptionContract(s)), errors => errors.ToProblem());
    }
    private static async Task<IResult> PreviewSubscription(Sqid datId, IDatSubscriptionService service, CancellationToken ct)
    {
        var result = await service.PreviewAsync(datId.Value, ct);
        return result.Match(p => Results.Ok(ReviewContract(p)), errors => errors.ToProblem());
    }
    private static async Task<IResult> ApplySubscription(Sqid datId, Models.ApplyDatSubscription request,
        IDatSubscriptionService service, CancellationToken ct)
    {
        var result = await service.ApplyAsync(datId.Value, request.ActiveSha256, request.CandidateSha256, ct);
        return result.Match(r => Results.Accepted(r.StatusUrl, new Models.UploadAccepted(
            r.JobId, r.BackgroundJobId, r.StatusUrl)), errors => errors.ToProblem());
    }
}
