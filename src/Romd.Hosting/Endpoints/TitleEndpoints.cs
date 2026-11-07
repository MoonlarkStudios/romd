using System.ComponentModel.DataAnnotations;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.GetArchiveFormats;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;
using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Admin.Application.Titles.Commands.SetPrimaryMedia;
using Romd.Admin.Application.Titles.Commands.SetTitleContentRating;
using Romd.Admin.Application.Titles.Commands.SetTitlesTracked;
using Romd.Admin.Application.Titles.Commands.SetTitleTracked;
using Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Admin.Application.Titles.Commands.UploadTitleMedia;
using Romd.Admin.Application.Titles.Queries.GetTitleSourceReferences;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Export;
using Romd.Contracts.Management.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Rom;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

using RomDeliveryFormat = Romd.Domain.Source.Rom.RomDeliveryFormat;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for Title operations. <c>Titles.Revision</c> is the validator of the core title family:
///     every write to metadata, content ratings, media, external IDs, merges and enrichment refreshes requires it in
///     <c>If-Match</c>, and each that answers with the title or a member of it returns the new one
///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"). Tracking and game moves change no
///     title row and carry no precondition.
/// </summary>
public static class TitleEndpoints
{
    /// <summary>The errorCode area of the title family's preconditions.</summary>
    internal const string PreconditionArea = "Titles";

    /// <summary>
    ///     Maps all Title-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapTitleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/titles")
            .WithTags("Titles");

        // Titles.Revision versions the core title family, not artwork, tracking or release facts, so the GET
        // emits the validator without revalidating (docs/decisions/api-contract-policy.md,
        // "Idempotency And Concurrency").
        group.MapGet("{titleId}", GetTitle)
            .WithName("GetTitle")
            .WithDescription("Get a title with its metadata, artwork, media, tracking, releases and file status")
            .Produces<Models.TitleDetail>()
            .ETagOnlyRead()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPatch("{titleId}", UpdateTitle)
            .WithName("UpdateTitle")
            .WithDescription("Partially update user-authored metadata: an absent field is unchanged, null removes its override")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("{titleId}/source-references", GetSourceReferences)
            .WithName("ListTitleSourceReferences")
            .WithDescription(
                "List the catalog sources backing a title, truth-level (all statuses) so curation surfaces can show dormant backing")
            .Produces<Page<Models.TitleSourceReference>>().ProducesProblems(400)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        // The refresh rotates the title revision, so it requires If-Match like every family write. Its 202 carries
        // no title representation, so it carries no ETag either: the client reloads the title.
        group.MapPost("{titleId}/enrich", TriggerEnrichment)
            .WithName("TriggerTitleEnrichment")
            .ProducesAcceptedJob()
            .ConditionalWriteWithoutETag()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPut("{titleId}/content-ratings/{board}", SetContentRating)
            .WithName("ReplaceTitleContentRating")
            .WithDescription("Set the user content rating override for one board")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapDelete("{titleId}/content-ratings/{board}", ClearContentRating)
            .WithName("DeleteTitleContentRating")
            .WithDescription("Clear the user content rating override for one board, reverting it to the provider cascade")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Media curation endpoints
        group.MapPost("{titleId}/media", UploadMedia)
            .WithName("UploadTitleMedia")
            .WithDescription("Upload custom media for a title. User uploads become the primary for their type.")
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesCreated<Models.TitleMediaRef>()
            .Produces<Models.TitleMediaRef>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            // Admin artwork uploads allow 32 MiB images, above the server default body limit.
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("{titleId}/media", ListMedia)
            .WithName("ListTitleMedia")
            .WithDescription("A keyset page of media assets for a title. limit is 1..100; cursors bind the title and id order.")
            .Produces<Page<Models.TitleMediaRef>>().ProducesProblems(400)
            .ETagOnlyRead()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{titleId}/releases", ListReleases).WithName("ListTitleReleases").Produces<Page<Models.TitleRelease>>().ProducesProblems(400, 404).RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("{titleId}/release-sources", ListReleaseSources).WithName("ListTitleReleaseSources").WithDescription("Per-asserting-DAT-entry release provenance, distinct from source-references' aggregated catalog sources. releaseId is the representative game identity from the title's releases; unknown or foreign releases are 404. limit is 1..100; cursors bind title, release and id order.").Produces<Page<Models.TitleReleaseSource>>().ProducesProblems(400, 404).RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{titleId}/media/{mediaId}", GetMedia)
            .WithName("GetTitleMedia")
            .WithDescription("Get one media asset of a title")
            .Produces<Models.TitleMediaRef>()
            .ETagOnlyRead()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPatch("{titleId}/media/{mediaId}", UpdateMedia)
            .WithName("UpdateTitleMedia")
            .WithDescription("Make a media item the primary for its type with { \"isPrimary\": true }")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapDelete("{titleId}/media/{mediaId}", DeleteMedia)
            .WithName("DeleteTitleMedia")
            .WithDescription("Delete a media file. If it was primary, the next best candidate is promoted.")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Curation endpoints
        group.MapPost("{titleId}/merge", MergeTitles)
            .WithName("MergeTitles")
            .WithDescription(
                "Merge a source title into this one. Source games move here, metadata is merged, the source is deleted.")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // A move rewrites source assignments, not either title, so it is not a member of the title family.
        group.MapPost("{titleId}/games/{gameId}/move", MoveGame)
            .WithName("MoveGame")
            .WithDescription("Move one of this title's games to an existing title, a new title, or no title.")
            .Produces<MoveGameResponse>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPut("{titleId}/tracking", SetTracking)
            .WithName("ReplaceTitleTracking")
            .WithDescription("Set whether a title is tracked (part of the curated collection target).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("batch-set-tracking", SetTrackingBatch)
            .WithName("BatchSetTitleTracking")
            .WithDescription("Set whether many titles are tracked in a single request.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    /// <summary>
    ///     Runs a core title write under the request's <c>If-Match</c> and answers with the title and its new ETag.
    ///     <paramref name="titleId" /> is the title the request addresses.
    /// </summary>
    internal static async Task<IResult> WriteAsync(HttpContext context, int titleId,
        Func<RevisionPrecondition, Task<ErrorOr<Models.TitleDetail>>> write)
    {
        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var written = await write(ifMatch.Value);
        return written.IsError ? written.Errors.ToProblem() : await TaggedAsync(context, titleId, written.Value);
    }

    /// <summary>
    ///     A title representation stamped with its validator. Every title response passes through here, so each lists the
    ///     archive formats the current user's title and release ZIPs offer, and a write's body lists what a read lists.
    /// </summary>
    internal static async Task<IResult> TaggedAsync(HttpContext context, int titleId, Models.TitleDetail detail)
    {
        var (title, releases) = await WithArchiveFormatsAsync(context, titleId, detail.Releases.Items);
        return Results.Ok(detail with
        {
            ArchiveFormats = title ?? [],
            Releases = new Page<Models.TitleRelease>
            {
                Items = releases,
                NextCursor = detail.Releases.NextCursor,
                HasNextPage = detail.Releases.HasNextPage
            }
        }).WithETag(context, detail.Revision);
    }

    /// <summary>
    ///     The archive formats of the title, when one is named, and each release with its own. A release without a
    ///     canonical id has no release ZIP, so it offers only original, like any ZIP the user cannot download.
    /// </summary>
    private static async Task<(IReadOnlyList<ArchiveFormat>? Title, IReadOnlyList<Models.TitleRelease> Releases)>
        WithArchiveFormatsAsync(HttpContext context, int? titleId, IReadOnlyList<Models.TitleRelease> releases)
    {
        var services = context.RequestServices;
        int?[] releaseIds = releases.Select(release => IdCoder.TryDecode(release.CatalogReleaseId, out int id) ? id : (int?)null).ToArray();
        var formats = (await services.GetRequiredService<IQueryHandler<GetArchiveFormatsQuery, ArchiveFormats>>()
            .HandleAsync(new(titleId, releaseIds.OfType<int>().ToArray()), context.RequestAborted)).Value;
        var originalOnly = ArchiveFormatMapping.ToContract([RomDeliveryFormat.Original],
            services.GetRequiredService<RomDeliveryPolicy>().Formats);
        return (formats.Title, releases.Select((release, index) => release with
        {
            ArchiveFormats = releaseIds[index] is { } id ? formats.Releases[id] : originalOnly
        }).ToArray());
    }

    internal static IResult TitleNotFound() => ErrorOrResultExtensions.ToProblem([CatalogErrors.TitleNotFound()]);

    private static async Task<IResult> GetTitle([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid titleId,
        ITitleDetailReader titleDetails,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var data = await titleDetails.GetPublicDetailAsync(titleId, cancellationToken);
        return data is null
            ? TitleNotFound()
            : await TaggedAsync(context, titleId, TitleDetailMapper.ToContract(data, systemKeys));
    }

    private static async Task<IResult> GetSourceReferences([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid titleId, string? cursor, ITitleSourceReferenceReader reader,
        CancellationToken cancellationToken, [Range(1, 100)] int limit = 50)
    {
        var result = await reader.GetPageAsync(titleId.Value, cursor, limit, cancellationToken);
        if (result.IsError)
            return result.Errors.ToProblem();
        var keys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        return Results.Ok(new Page<Models.TitleSourceReference> { Items = result.Value.Items.Select(item => item.ToContract(keys)).ToArray(), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage });
    }

    private static async Task<IResult> TriggerEnrichment(Sqid titleId,
        HttpContext context,
        ICommandHandler<TriggerTitleEnrichmentCommand, Guid> handler,
        CancellationToken cancellationToken)
    {
        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        if (ifMatch.IsError)
        {
            return ifMatch.Errors.ToProblem();
        }

        var result = await handler.HandleAsync(
            new TriggerTitleEnrichmentCommand(titleId, IfMatch: ifMatch.Value),
            cancellationToken);
        return result.ToAccepted();
    }

    private static Task<IResult> UpdateTitle(Sqid titleId,
        UpdateTitleMetadataRequest request,
        HttpContext context,
        ICommandHandler<UpdateUserMetadataCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(context, titleId, ifMatch => handler.HandleAsync(
            new UpdateUserMetadataCommand(
                titleId,
                new TitleMetadataPayload
                {
                    Name = request.Name,
                    Description = request.Description,
                    Publisher = request.Publisher,
                    Developer = request.Developer,
                    Genre = request.Genre,
                    ReleaseDate = request.ReleaseDate,
                    Players = request.Players,
                    Rating = request.Rating
                },
                // The patch converter admits only the request's own field names, which name the flags.
                request.Changes.Keys.Aggregate(TitleMetadataFields.None,
                    (fields, field) => fields | Enum.Parse<TitleMetadataFields>(field, ignoreCase: true)),
                ifMatch),
            cancellationToken));

    private static Task<IResult> SetContentRating(Sqid titleId,
        string board,
        SetTitleContentRatingRequest request,
        HttpContext context,
        ICommandHandler<SetTitleContentRatingCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        if (ParseBoard(board) is not { } ratingBoard)
        {
            return Task.FromResult(ReferenceCatalogEndpoints.UnknownReference());
        }

        // Clearing is DELETE on this route; a blank code would clear through PUT.
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Task.FromResult(ErrorOrResultExtensions.ToProblem(
                [CatalogErrors.InvalidContentRating(ratingBoard, request.Code ?? "")]));
        }

        return WriteAsync(context, titleId, ifMatch => handler.HandleAsync(
            new SetTitleContentRatingCommand(titleId, ratingBoard, request.Code, request.Descriptors, request.Synopsis, ifMatch),
            cancellationToken));
    }

    private static Task<IResult> ClearContentRating(Sqid titleId,
        string board,
        HttpContext context,
        ICommandHandler<SetTitleContentRatingCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken) =>
        ParseBoard(board) is { } ratingBoard
            ? WriteAsync(context, titleId, ifMatch => handler.HandleAsync(
                new SetTitleContentRatingCommand(titleId, ratingBoard, null, null, null, ifMatch),
                cancellationToken))
            : Task.FromResult(ReferenceCatalogEndpoints.UnknownReference());

    /// <summary>
    ///     A rating board route key: the contract's named board, matched exactly as the rating-board reference
    ///     resources key it. Enum binding would also accept numeric strings.
    /// </summary>
    private static Romd.Domain.Catalog.Ratings.RatingBoard? ParseBoard(string board) =>
        Enum.GetNames<Models.RatingBoard>().Contains(board, StringComparer.Ordinal)
            ? Enum.Parse<Models.RatingBoard>(board).ToDomain()
            : null;

    private static async Task<IResult> UploadMedia(Sqid titleId,
        IFormFile file,
        string type,
        HttpContext context,
        ICommandHandler<UploadTitleMediaCommand, UploadedTitleMedia> handler,
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

        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        if (ifMatch.IsError)
        {
            return ifMatch.Errors.ToProblem();
        }

        // Get the file stream
        await using var stream = file.OpenReadStream();

        var command = new UploadTitleMediaCommand(
            titleId,
            mediaType,
            stream,
            file.FileName,
            ifMatch.Value);

        var result = await handler.HandleAsync(command, cancellationToken);

        // A new item is 201 at its own URL and rotates the title's validator. A repeat of the same content and type
        // under the current ETag is the 200 no-op: it writes nothing and answers the existing item. A retry with a
        // stale ETag is a 412. Either way the ETag is the title's current one.
        return result.Match(
            uploaded => (uploaded.Created
                    ? Results.Created($"/api/titles/{titleId}/media/{uploaded.Media.Id}", uploaded.Media)
                    : Results.Ok(uploaded.Media))
                .WithETag(context, uploaded.TitleRevision),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> ListMedia(Sqid titleId, string? cursor, ITitleDetailReader titleDetails,
        HttpContext context, CancellationToken cancellationToken, [Range(1, 100)] int limit = 50)
    {
        var result = await titleDetails.GetMediaPageAsync(titleId.Value, cursor, limit, cancellationToken);
        return result.Match(page => Results.Ok(new Page<Models.TitleMediaRef> { Items = page.Page.Items.Select(ToMediaRef).ToArray(), NextCursor = page.Page.NextCursor, HasNextPage = page.Page.HasNextPage }).WithETag(context, page.Revision.ToString()), errors => errors.ToProblem());
    }

    private static async Task<IResult> ListReleases(Sqid titleId, string? cursor, ITitleDetailReader titleDetails,
        HttpContext context, CancellationToken ct, [Range(1, 100)] int limit = 50)
    {
        var result = await titleDetails.GetReleasePageAsync(titleId.Value, cursor, limit, ct);
        if (result.IsError)
            return result.Errors.ToProblem();
        var (_, items) = await WithArchiveFormatsAsync(context, null,
            result.Value.Page.Items.Select(release => TitleDetailMapper.ToReleaseContract(release, titleId.Value)).ToArray());
        return Results.Ok(new Page<Models.TitleRelease> { Items = items, NextCursor = result.Value.Page.NextCursor, HasNextPage = result.Value.Page.HasNextPage }).WithETag(context, result.Value.Revision.ToString());
    }

    private static async Task<IResult> ListReleaseSources(Sqid titleId, string? releaseId, string? cursor, ITitleDetailReader titleDetails,
        HttpContext context, CancellationToken ct, [Range(1, 100)] int limit = 50)
    {
        var result = await titleDetails.GetReleaseSourcesPageAsync(titleId.Value, releaseId, cursor, limit, ct);
        return result.Match(page => Results.Ok(new Page<Models.TitleReleaseSource> { Items = page.Page.Items.Select(source => new Models.TitleReleaseSource { DatGameId = IdCoder.Encode(source.DatGameId), DatId = IdCoder.Encode(source.DatFileId), DatName = source.DatName, GameName = source.GameName }).ToArray(), NextCursor = page.Page.NextCursor, HasNextPage = page.Page.HasNextPage }).WithETag(context, page.Revision.ToString()), errors => errors.ToProblem());
    }

    private static async Task<IResult> GetMedia(Sqid titleId,
        Sqid mediaId,
        ITitleDetailReader titleDetails,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        string? cursor = mediaId.Value > 1 ? Romd.Application.Common.Pagination.KeysetCursor.Encode(
            new Romd.Application.Common.Pagination.IntegerPosition(mediaId.Value - 1),
            Romd.Application.Common.Pagination.KeysetCursor.Fingerprint("admin.title.media", titleId.Value, "id:asc")) : null;
        var page = await titleDetails.GetMediaPageAsync(titleId.Value, cursor, 1, cancellationToken);
        if (page.IsError)
            return page.Errors.ToProblem();
        return page.Value.Page.Items.SingleOrDefault(media => media.Id == mediaId.Value) is { } item
            ? Results.Ok(ToMediaRef(item)).WithETag(context, page.Value.Revision.ToString())
            : ErrorOrResultExtensions.ToProblem([CatalogErrors.MediaNotFound()]);
    }

    private static Models.TitleMediaRef ToMediaRef(TitleMediaData media) => new()
    {
        Id = IdCoder.Encode(media.Id),
        Type = media.Type,
        Url = $"/media/{IdCoder.Encode(media.Id)}",
        SourceId = media.SourceId,
        Attribution = media.Attribution,
        SourcePageUrl = media.SourcePageUrl,
        File = Romd.Admin.Application.Storage.StoredFileRefMapping.ToStoredFileRef(media.SizeBytes, media.SizeOnDiskBytes, media.IsCompressed),
        IsPrimary = media.IsPrimary
    };

    private static Task<IResult> UpdateMedia(Sqid titleId,
        Sqid mediaId,
        UpdateTitleMediaRequest request,
        HttpContext context,
        ICommandHandler<SetPrimaryMediaCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken) =>
        request.IsPrimary
            ? WriteAsync(context, titleId, ifMatch => handler.HandleAsync(
                new SetPrimaryMediaCommand(titleId, mediaId, ifMatch),
                cancellationToken))
            : Task.FromResult(ProblemResults.ValidationProblem(
                "Titles.InvalidMediaUpdate",
                "isPrimary",
                "Only { \"isPrimary\": true } is supported: make another item primary instead of clearing one."));

    private static Task<IResult> DeleteMedia(Sqid titleId,
        Sqid mediaId,
        HttpContext context,
        ICommandHandler<DeleteTitleMediaCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(context, titleId, ifMatch => handler.HandleAsync(
            new DeleteTitleMediaCommand(titleId, mediaId, ifMatch),
            cancellationToken));

    private static async Task<IResult> MergeTitles(Sqid titleId,
        MergeTitlesRequest request,
        HttpContext context,
        ICommandHandler<MergeTitlesCommand, Models.TitleDetail> handler,
        CancellationToken cancellationToken)
    {
        if (RequestBodyIds.Decode(request.SourceTitleId, "sourceTitleId", "Titles.InvalidTitleId", out int sourceId) is { } invalid)
        {
            return invalid;
        }

        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        if (ifMatch.IsError)
        {
            return ifMatch.Errors.ToProblem();
        }

        var command = new MergeTitlesCommand(sourceId, titleId, ifMatch.Value, request.SourceTitleRevision);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.IsError
            ? BatchRevisionProblem(result.Errors, CatalogErrors.MergeSourceChangedCode, "sourceTitleRevision")
            : await TaggedAsync(context, titleId, result.Value);
    }

    private static async Task<IResult> MoveGame(Sqid titleId,
        Sqid gameId,
        MoveGameRequest request,
        ICommandHandler<MoveGameCommand, MoveGameResult> handler,
        CancellationToken cancellationToken)
    {
        if (RequestBodyIds.DecodeOptional(request.TargetTitleId, "targetTitleId", "Titles.InvalidTargetTitleId",
                out int? targetTitleId) is { } invalid)
        {
            return invalid;
        }

        var command = new MoveGameCommand(titleId, gameId, targetTitleId, request.NewTitleName);
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

    /// <summary>
    ///     A stale batch revision is a 409 with the failure keyed by the item's field, never a 412
    ///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
    /// </summary>
    private static IResult BatchRevisionProblem(IReadOnlyList<Error> errors, string batchConflictCode, string field) =>
        errors is [{ Code: var code, Description: var description }, ..] && code == batchConflictCode
            ? ProblemResults.Problem(StatusCodes.Status409Conflict, code, description,
                new Dictionary<string, string[]> { [field] = [description] })
            : errors.ToProblem();

    private static async Task<IResult> SetTracking(Sqid titleId,
        SetTitleTrackedRequest request,
        ICommandHandler<SetTitleTrackedCommand, Updated> handler,
        CancellationToken cancellationToken)
    {
        if (request.PinnedCatalogReleaseId is not null && !request.Tracked)
        {
            return ProblemResults.ValidationProblem(
                "Titles.InvalidPinnedCatalogReleaseId",
                "pinnedCatalogReleaseId",
                "Only a tracking request can pin a release.");
        }

        if (RequestBodyIds.DecodeOptional(request.PinnedCatalogReleaseId, "pinnedCatalogReleaseId",
                "Titles.InvalidPinnedCatalogReleaseId", out int? pinnedCatalogReleaseId) is { } invalid)
        {
            return invalid;
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
        var (unknownSystem, platformId) = await referenceCatalog.ResolveOptionalBodySystemKeyAsync(
            request.SystemKey, "systemKey", cancellationToken);
        if (unknownSystem is not null)
        {
            return unknownSystem;
        }

        var invalidIds = new Dictionary<string, string[]>();
        var titleIds = RequestBodyIds.Collect(request.TitleIds ?? [], "titleIds", invalidIds);
        int? datSourceId = RequestBodyIds.CollectOptional(request.DatSourceId, "datSourceId", invalidIds);
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
