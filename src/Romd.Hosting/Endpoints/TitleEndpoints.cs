using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.AssociateExternalId;
using Romd.Admin.Application.Titles.Commands.ConfirmExternalId;
using Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;
using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Admin.Application.Titles.Commands.SetPrimaryMedia;
using Romd.Admin.Application.Titles.Commands.SetTitleContentRating;
using Romd.Admin.Application.Titles.Commands.SetTitleTracked;
using Romd.Admin.Application.Titles.Commands.SetTitlesTracked;
using Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Admin.Application.Titles.Commands.UploadTitleMedia;
using Romd.Admin.Application.Titles.Queries.GetTitleSourceReferences;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Titles;
using Romd.Domain.Catalog;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for Title operations.
/// </summary>
public static class TitleEndpoints
{
    /// <summary>
    ///     Maps all Title-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapTitleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/titles")
            .WithTags("Titles");

        group.MapGet("{titleId}", GetById)
            .WithName("GetTitleById")
            .Produces<Models.Title>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{titleId}/details", GetDetails)
            .WithName("GetTitleDetails")
            .WithDescription("Get comprehensive title details including releases and file status")
            .Produces<Models.TitleDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{titleId}/source-references", GetSourceReferences)
            .WithName("GetTitleSourceReferences")
            .WithDescription(
                "List the catalog sources backing a title, truth-level (all statuses) so curation surfaces can show dormant backing")
            .Produces<List<Models.TitleSourceReference>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("{titleId}/enrich", TriggerEnrichment)
            .WithName("TriggerTitleEnrichment")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPatch("{titleId}/metadata", UpdateMetadata)
            .WithName("UpdateTitleMetadata")
            .WithDescription("Update user-authored metadata for a title")
            .Produces<Models.TitleDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPatch("{titleId}/content-rating", SetContentRating)
            .WithName("SetTitleContentRating")
            .WithDescription("Set or clear the user content rating override for a single board")
            .Produces<Models.TitleDetail>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{titleId}/external-ids", AssociateExternalId)
            .WithName("AssociateExternalId")
            .WithDescription("Manually associate a title with an external provider ID")
            .Produces<Models.TitleDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{titleId}/external-ids/{provider}/confirm", ConfirmExternalId)
            .WithName("ConfirmExternalId")
            .WithDescription("Confirm an auto-matched external ID, making it immutable to auto-enrichment")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Media curation endpoints
        group.MapPost("{titleId}/media", UploadMedia)
            .WithName("UploadTitleMedia")
            .WithDescription("Upload custom media for a title. User uploads become the primary for their type.")
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.TitleMediaRef>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("{titleId}/media", ListMedia)
            .WithName("ListTitleMedia")
            .WithDescription("List all media assets for a title (from all sources)")
            .Produces<List<Models.TitleMediaRef>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPut("{titleId}/media/{mediaId}/primary", SetPrimaryMedia)
            .WithName("SetPrimaryMedia")
            .WithDescription("Manually set a specific media as the primary for its type")
            .Produces<Models.TitleDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapDelete("{titleId}/media/{mediaId}", DeleteMedia)
            .WithName("DeleteTitleMedia")
            .WithDescription("Delete a media file. If it was primary, the next best candidate is promoted.")
            .Produces<Models.TitleDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Curation endpoints
        group.MapPost("merge", MergeTitles)
            .WithName("MergeTitles")
            .WithDescription(
                "Merge source title into target. Source games move to target, metadata is merged, source is deleted.")
            .Produces<Models.TitleDetail>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("games/{gameId}/move", MoveGame)
            .WithName("MoveGame")
            .WithDescription("Move a game to an existing title, create a new title, or unassign from any title.")
            .Produces<MoveGameResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPut("{titleId}/tracking", SetTracking)
            .WithName("SetTitleTracking")
            .WithDescription("Set whether a title is tracked (part of the curated collection target).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPut("tracking", SetTrackingBatch)
            .WithName("SetTitlesTracking")
            .WithDescription("Set whether many titles are tracked in a single request.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<IResult> GetById([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid titleId,
        ITitleRepository titleRepository,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var title = await titleRepository.GetByIdAsync(titleId, cancellationToken);
        if (title is null)
        {
            return Results.NotFound();
        }

        bool hasLocalPayload = await titleRepository.HasLocalPayloadAsync(titleId, cancellationToken);
        return Results.Ok(title.ToContract(systemKeys, hasLocalPayload));
    }

    private static async Task<IResult> GetDetails([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid titleId,
        ITitleRepository titleRepository,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var data = await titleRepository.GetTitleDetailAsync(titleId, cancellationToken);
        if (data is null)
        {
            return Results.NotFound();
        }

        var response = TitleDetailMapper.ToContract(data, systemKeys);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetSourceReferences([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid titleId,
        IQueryHandler<GetTitleSourceReferencesQuery, IReadOnlyList<TitleSourceReference>> handler,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var result = await handler.HandleAsync(new GetTitleSourceReferencesQuery(titleId), cancellationToken);
        return result.Match(
            references => Results.Ok(references.ToContract(systemKeys).ToList()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> TriggerEnrichment(Sqid titleId,
        ICommandHandler<TriggerTitleEnrichmentCommand, Guid> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new TriggerTitleEnrichmentCommand(titleId),
            cancellationToken);
        return result.Match(
            _ => Results.Accepted(),
            errors => errors[0].Type == ErrorType.NotFound ? Results.NotFound() : errors.ToProblem());
    }

    private static async Task<IResult> UpdateMetadata(Sqid titleId,
        UpdateTitleMetadataRequest request,
        ICommandHandler<UpdateUserMetadataCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var command = new UpdateUserMetadataCommand(
            titleId,
            request.Name,
            request.Description,
            request.Publisher,
            request.Developer,
            request.Genre,
            request.ReleaseDate,
            request.Players,
            request.Rating);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            titleDetail => Results.Ok(titleDetail),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> SetContentRating(Sqid titleId,
        SetTitleContentRatingRequest request,
        ICommandHandler<SetTitleContentRatingCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        // JSON binding enforces board validity: the admin serializer rejects unknown names and
        // integer values, and the exception-handling path converts the failure into a 400
        // envelope with errorCode "Request.InvalidBody".
        var command = new SetTitleContentRatingCommand(
            titleId,
            request.Board.ToDomain(),
            request.Code,
            request.Descriptors,
            request.Synopsis);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            titleDetail => Results.Ok(titleDetail),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> AssociateExternalId(Sqid titleId,
        AssociateExternalIdRequest request,
        ICommandHandler<AssociateExternalIdCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var command = new AssociateExternalIdCommand(
            titleId,
            request.Provider,
            request.ExternalId);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            titleDetail => Results.Ok(titleDetail),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> ConfirmExternalId(Sqid titleId,
        string provider,
        ICommandHandler<ConfirmExternalIdCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        var command = new ConfirmExternalIdCommand(
            titleId,
            provider);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> UploadMedia(Sqid titleId,
        IFormFile file,
        string type,
        ICommandHandler<UploadTitleMediaCommand, Models.TitleMediaRef> handler,
        CancellationToken cancellationToken)
    {
        // Parse the media type
        if (!Enum.TryParse<MediaType>(type, true, out var mediaType))
        {
            return ProblemResults.ValidationProblem(
                "Titles.InvalidMediaType",
                "type",
                $"'{type}' is not a valid media type. Accepted values: Cover, Screenshot, Banner, Logo, Background, Video, Box3d, TitleScreen.");
        }

        // Get the file stream
        await using var stream = file.OpenReadStream();

        var command = new UploadTitleMediaCommand(
            titleId,
            mediaType,
            stream,
            file.FileName);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            mediaRef => Results.Ok(mediaRef),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> ListMedia(Sqid titleId,
        ITitleRepository titleRepository,
        CancellationToken cancellationToken)
    {
        var data = await titleRepository.GetTitleDetailAsync(titleId, cancellationToken);
        if (data is null)
        {
            return Results.NotFound();
        }

        var mediaList = data.Media.Select(m => new Models.TitleMediaRef
        {
            Id = IdCoder.Encode(m.Id),
            Type = m.Type,
            Url = $"/media/{IdCoder.Encode(m.Id)}",
            SourceId = m.SourceId,
            IsPrimary = m.IsPrimary
        }).ToList();

        return Results.Ok(mediaList);
    }

    private static async Task<IResult> SetPrimaryMedia(Sqid titleId,
        Sqid mediaId,
        ICommandHandler<SetPrimaryMediaCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var command = new SetPrimaryMediaCommand(
            titleId,
            mediaId);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            titleDetail => Results.Ok(titleDetail),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> DeleteMedia(Sqid titleId,
        Sqid mediaId,
        ICommandHandler<DeleteTitleMediaCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var command = new DeleteTitleMediaCommand(
            titleId,
            mediaId);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            titleDetail => Results.Ok(titleDetail),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> MergeTitles(MergeTitlesRequest request,
        ICommandHandler<MergeTitlesCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        var invalidIds = new Dictionary<string, string[]>();
        if (!IdCoder.TryDecode(request.SourceTitleId, out int sourceId))
        {
            invalidIds["sourceTitleId"] = [$"'{request.SourceTitleId}' is not a valid title ID."];
        }

        if (!IdCoder.TryDecode(request.TargetTitleId, out int targetId))
        {
            invalidIds["targetTitleId"] = [$"'{request.TargetTitleId}' is not a valid title ID."];
        }

        if (invalidIds.Count > 0)
        {
            return ProblemResults.ValidationProblem("Titles.InvalidTitleId", invalidIds);
        }

        var command = new MergeTitlesCommand(sourceId, targetId);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            titleDetail => Results.Ok(titleDetail),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> MoveGame(Sqid gameId,
        MoveGameRequest request,
        ICommandHandler<MoveGameCommand, MoveGameResult> handler,
        CancellationToken cancellationToken)
    {
        int? targetTitleId = null;
        if (!string.IsNullOrWhiteSpace(request.TargetTitleId))
        {
            if (!IdCoder.TryDecode(request.TargetTitleId, out int decoded))
            {
                return ProblemResults.ValidationProblem(
                    "Titles.InvalidTargetTitleId",
                    "targetTitleId",
                    $"'{request.TargetTitleId}' is not a valid title ID.");
            }

            targetTitleId = decoded;
        }

        var command = new MoveGameCommand(gameId, targetTitleId, request.NewTitleName);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            moveResult => Results.Ok(new MoveGameResponse
            {
                GameId = moveResult.GameId,
                NewTitle = moveResult.NewTitle,
                TitleCreated = moveResult.TitleCreated,
                OldTitleDeleted = moveResult.OldTitleDeleted
            }),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> SetTracking(Sqid titleId,
        SetTitleTrackedRequest request,
        ICommandHandler<SetTitleTrackedCommand, Updated> handler,
        CancellationToken cancellationToken)
    {
        int? pinnedCatalogReleaseId = null;
        if (!string.IsNullOrWhiteSpace(request.PinnedCatalogReleaseId))
        {
            if (!request.Tracked || !IdCoder.TryDecode(request.PinnedCatalogReleaseId, out int decoded))
            {
                return ProblemResults.ValidationProblem(
                    "Titles.InvalidPinnedCatalogReleaseId",
                    "pinnedCatalogReleaseId",
                    $"'{request.PinnedCatalogReleaseId}' is not a valid pin for this tracking request.");
            }

            pinnedCatalogReleaseId = decoded;
        }

        var command = new SetTitleTrackedCommand(titleId, request.Tracked, pinnedCatalogReleaseId);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> SetTrackingBatch([FromServices] IReferenceCatalogService referenceCatalog,
        BatchSetTitleTrackedRequest request,
        ICommandHandler<SetTitlesTrackedCommand, Updated> handler,
        CancellationToken cancellationToken)
    {
        var invalidIds = new Dictionary<string, string[]>();
        var titleIds = new List<int>();
        foreach (string id in request.TitleIds ?? [])
        {
            if (IdCoder.TryDecode(id, out int decoded))
            {
                titleIds.Add(decoded);
            }
            else
            {
                invalidIds["titleIds"] = [$"'{id}' is not a valid title ID."];
                break;
            }
        }

        int? platformId = request.SystemKey is null ? null : await referenceCatalog.RequireSystemAsync(request.SystemKey, cancellationToken);

        int? datSourceId = null;
        if (!string.IsNullOrWhiteSpace(request.DatSourceId))
        {
            if (IdCoder.TryDecode(request.DatSourceId, out int decoded))
            {
                datSourceId = decoded;
            }
            else
            {
                invalidIds["datSourceId"] = [$"'{request.DatSourceId}' is not a valid DAT source ID."];
            }
        }

        if (invalidIds.Count > 0)
        {
            return ProblemResults.ValidationProblem("Titles.InvalidTrackingSelector", invalidIds);
        }

        var command = new SetTitlesTrackedCommand(
            titleIds.Count > 0 ? titleIds : null,
            platformId,
            datSourceId,
            request.Tracked);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }
}
