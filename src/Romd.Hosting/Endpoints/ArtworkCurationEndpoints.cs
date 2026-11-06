using System.Text.Json.Nodes;
using System.ComponentModel.DataAnnotations;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Security;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Artwork.Commands.ReplaceSteamGridDbSettings;
using Romd.Admin.Application.Artwork.Commands.TestSteamGridDbConnection;
using Romd.Admin.Application.Artwork.Commands.PinTitleMediaArtwork;
using Romd.Admin.Application.Artwork.Commands.QueueArtworkCompletion;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     A title's artwork under <c>/api/titles/{titleId}</c>
///     (docs/decisions/api-contract-policy.md, "Resource Model"). Each role at <c>…/artwork/{role}</c> is versioned
///     by its own revision. The family root <c>…/artwork</c> carries a validator built from every role revision, and
///     it guards the family's saved assets,
///     rejections and hidden candidates, whose writes answer the root with its new validator
///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"). Neither validator revalidates: an
///     import publishes its pin, and a failed one clears its pending job, under the revision the request rotated.
/// </summary>
public static class ArtworkCurationEndpoints
{
    /// <summary>The errorCode area of artwork preconditions.</summary>
    internal const string PreconditionArea = "Artwork";

    public static IEndpointRouteBuilder MapArtworkCurationEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/artwork-providers/steamgriddb/settings")
            .WithTags("ArtworkSettings").RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        settings.MapGet("", async (ISteamGridDbSettingsService service, HttpContext context, CancellationToken ct) =>
            {
                var state = await service.GetAsync(ct);
                return Results.Ok(state).WithETag(context, state.Revision.ToString());
            })
            .WithName("GetSteamGridDbSettings").Produces<SteamGridDbSettingsDto>().ETagOnlyRead();
        settings.MapPut("", ReplaceSteamGridSettings)
            .WithName("ReplaceSteamGridDbSettings").Produces<SteamGridDbSettingsDto>().ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);
        settings.MapPost("/test-connection", TestSteamGridConnection)
            .WithName("TestSteamGridDbConnection").Produces<SteamGridDbSettingsDto>().ConditionalWrite();

        var group = app.MapGroup("/artwork").WithTags("Artwork")
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("/completion/preview", CompletionPreview).WithName("PreviewArtworkCompletion")
            .Produces<ArtworkCompletionPreviewDto>().ProducesProblem(400).ProducesProblem(404);
        group.MapPost("/completion", QueueCompletion).WithName("QueueArtworkCompletion")
            .ProducesAcceptedJob().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/artwork-providers", Providers).WithTags("Artwork").WithName("ListArtworkProviders")
            .WithDescription("The configured provider registry (at most the two built-in adapters). A role filters each provider's capabilities; a title additionally requires an available trusted provider match.")
            .Produces<IReadOnlyList<ArtworkProviderDto>>().ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        var title = app.MapGroup("/titles/{titleId}").WithTags("Artwork")
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        title.MapGet("artwork", GetFamily).WithName("GetTitleArtwork")
            .WithDescription("Every artwork role's selection, with the family validator that guards assets, rejections and hidden candidates")
            .Produces<TitleArtworkDto>().ETagOnlyRead().ProducesProblems(StatusCodes.Status404NotFound);
        title.MapGet("artwork/{role}", GetRole).WithName("GetTitleArtworkRole").WithRoleValues()
            .Produces<ArtworkSelectionStateDto>().ETagOnlyRead().ProducesProblems(StatusCodes.Status404NotFound);
        title.MapPut("artwork/{role}", ReplaceRole).WithName("ReplaceTitleArtworkRole").WithRoleValues()
            .WithDescription("Set how a role chooses its artwork: automatically, left empty, or pinned to a saved asset or title media item")
            .Produces<ArtworkSelectionStateDto>().ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        title.MapPost("artwork/{role}/apply", Apply).WithName("ApplyArtworkCandidate").WithRoleValues()
            .WithDescription("Queue the import of a provider candidate into the role")
            .ProducesAcceptedJob().ConditionalWriteWithoutETag()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        title.MapGet("artwork-assets", ListAssets).WithName("ListTitleArtworkAssets")
            .WithDescription("Saved eligible images in creation-time/id ascending order. Limit defaults to 50 and is at most 100; every page carries the aggregate artwork-family ETag.")
            .Produces<Page<SavedArtworkDto>>().ETagOnlyRead().ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        title.MapDelete("artwork-assets/{assetId}", DeleteAsset).WithName("DeleteTitleArtworkAsset")
            .WithDescription("Delete a saved image from the title; roles showing it return to automatic")
            .Produces<TitleArtworkDto>().ConditionalWrite().ProducesProblems(StatusCodes.Status404NotFound);
        title.MapGet("artwork-rejections", ListRejections).WithName("ListTitleArtworkRejections")
            .WithDescription("Rejected images, including ineligible images, in id ascending order. Limit defaults to 50 and is at most 100; every page carries the aggregate artwork-family ETag.")
            .Produces<Page<RejectedArtworkDto>>().ETagOnlyRead().ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        title.MapPut("artwork-rejections/{assetId}", Reject).WithName("ReplaceTitleArtworkRejection")
            .WithDescription("Reject a saved asset, and every version of its provider candidate, for its role")
            .Produces<TitleArtworkDto>().ConditionalWrite().ProducesProblems(StatusCodes.Status404NotFound);
        title.MapDelete("artwork-rejections/{assetId}", Restore).WithName("DeleteTitleArtworkRejection")
            .WithDescription("Restore a rejected asset to its role's choices")
            .Produces<TitleArtworkDto>().ConditionalWrite().ProducesProblems(StatusCodes.Status404NotFound);
        // A hidden candidate is keyed by its stable candidate id; the signed reference proves which candidate it is.
        title.MapPut("hidden-artwork-candidates/{candidateId}", Hide).WithName("ReplaceHiddenArtworkCandidate")
            .WithDescription("Hide a provider candidate from this title's choices and automatic acquisition")
            .Produces<TitleArtworkDto>().ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        // Upstream provider failures are 503, or 502 for an invalid provider response.
        title.MapGet("artwork-candidates", Browse).WithName("ListTitleArtworkCandidates")
            .WithDescription("Transient candidates follow provider-owned ordering using signed title/user/filter/expiry-bound continuations, rather than persisted keysets. SteamGridDB serves at most 50 raw candidates per page and 100 pages; IGDB serves one response of at most 500 raw images across image kinds. Responses are limited to 2 MiB and 30 seconds. Exceeding an adapter budget is an error, never reported as exhaustion.")
            .Produces<Page<ArtworkCandidateDto>>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict,
                StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable);
        title.MapPost("artwork-candidates/preview", Preview).WithName("PreviewArtworkCandidate")
            .Produces(200, contentType: "image/png")
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict,
                StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable);
        // The import adds a member of the core title family, so it runs under the title's validator and answers
        // the created media with the title's new ETag.
        title.MapPost("artwork-candidates/{candidateId}/import", ImportGallery).WithName("ImportGalleryArtwork")
            .WithDescription("Save a provider candidate's original to the title's media without changing any role")
            .ProducesCreated<Models.TitleMediaRef>().Produces<Models.TitleMediaRef>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict,
                StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<IResult> CompletionPreview(ArtworkCompletionRequest request, IArtworkCompletionService service, CancellationToken ct) =>
        (await service.PreviewScopeAsync(request, ct)).Match(Results.Ok, errors => errors.ToProblem());
    private static async Task<IResult> QueueCompletion(ArtworkCompletionRequest request,
        ICommandHandler<QueueArtworkCompletionCommand, Guid> handler, ICurrentUser user, CancellationToken ct)
    {
        if (user.UserId is not { } actor)
            return ActorRequired();
        return (await handler.HandleAsync(new(request, actor), ct)).ToAccepted();
    }
    private static async Task<IResult> Providers(IArtworkBrowsingService service, ITitleProviderMatchService matches,
        CancellationToken ct, Sqid? titleId = null, ArtworkRole? role = null)
    {
        var states = await service.GetCapabilitiesAsync(ct);
        if (titleId is { } title)
        {
            var links = await matches.GetAsync(title.Value, ct);
            if (links.IsError)
                return links.Errors.ToProblem();
            var available = new List<ArtworkProviderDto>();
            foreach (var state in states.Where(state => state.IsAvailable))
            {
                var link = links.Value.SingleOrDefault(link => link.ProviderId == state.Provider.ProviderId && link.IsAvailable);
                if (link?.ExternalId is not { } gameId ||
                    await matches.GetLinkRevisionAsync(title.Value, link.ProviderId, gameId, ct) is null)
                    continue;
                var roles = state.Provider.Roles.Where(item => role is null || item.Role == role).ToArray();
                if (roles.Length == 0)
                    continue;
                available.Add(new(state.Provider.ProviderId, state.Provider.Name, true, state.Provider.SupportsLanguageFilter,
                    roles.Select(item => new ArtworkRoleCapabilityDto(item.Role.ToString(), item.Dimensions, item.Styles)).ToArray(),
                    gameId, link.Game?.Name, link.Game?.Url, state.Provider.MediaTypes?.Select(type => type.ToString()).ToArray()));
            }
            return Results.Ok(available);
        }
        return Results.Ok(states.Where(state => role is null || state.Provider.Roles.Any(item => item.Role == role))
            .Select(state => new ArtworkProviderDto(state.Provider.ProviderId, state.Provider.Name, state.IsAvailable,
            state.Provider.SupportsLanguageFilter, state.Provider.Roles.Where(item => role is null || item.Role == role).Select(item => new ArtworkRoleCapabilityDto(
                item.Role.ToString(), item.Dimensions, item.Styles)).ToArray(),
            MediaTypes: state.Provider.MediaTypes?.Select(type => type.ToString()).ToArray())).ToArray());
    }

    private static async Task<IResult> GetFamily(Sqid titleId, HttpContext context, IArtworkCurationRepository repository,
        CancellationToken ct) =>
        (await repository.GetFamilyAsync(titleId.Value, ct)).Match(family => FamilyTagged(context, family),
            errors => errors.ToProblem());

    private static async Task<IResult> GetRole(Sqid titleId, string role, HttpContext context,
        IArtworkCurationRepository repository, CancellationToken ct)
    {
        if (ParseRole(role) is not { } parsed)
            return UnknownRole();
        return (await repository.GetStatesAsync(titleId.Value, ct)).Match(
            states => RoleTagged(context, states.Single(state => state.Role == parsed)), errors => errors.ToProblem());
    }

    private static async Task<IResult> ReplaceRole(Sqid titleId, string role, ReplaceArtworkSelectionRequest request,
        HttpContext context, ArtworkCurationService service,
        ICommandHandler<PinTitleMediaArtworkCommand, ArtworkCurationState> pinMedia, CancellationToken ct)
    {
        if (ParseRole(role) is not { } parsed)
            return UnknownRole();
        var failures = new Dictionary<string, string[]>();
        int? assetId = RequestBodyIds.CollectOptional(request.AssetId, "assetId", failures);
        int? mediaId = RequestBodyIds.CollectOptional(request.MediaId, "mediaId", failures);
        CollectSelectionFailures(request, failures);
        if (failures.Count > 0)
            return ProblemResults.ValidationProblem("Artwork.InvalidSelection", failures);
        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        int focalX = request.FocalX ?? 50, focalY = request.FocalY ?? 50;
        var result = request.Mode switch
        {
            ArtworkMode.Automatic => await service.ReturnToAutomaticAsync(titleId.Value, parsed, ifMatch.Value, ct),
            ArtworkMode.Empty => await service.LeaveEmptyAsync(titleId.Value, parsed, ifMatch.Value, ct),
            _ when assetId is { } asset => await service.PinAsync(titleId.Value, parsed, asset, ifMatch.Value, ct, focalX, focalY),
            _ => await pinMedia.HandleAsync(new(titleId.Value, parsed, mediaId!.Value, ifMatch.Value, focalX, focalY), ct)
        };
        return result.Match(state => RoleTagged(context, state), errors => errors.ToProblem());
    }

    /// <summary>
    ///     A pinned role names exactly one saved asset or title media item and may place its focal point; automatic
    ///     and empty roles take none of these. Every failure is reported by its field.
    /// </summary>
    private static void CollectSelectionFailures(ReplaceArtworkSelectionRequest request, Dictionary<string, string[]> failures)
    {
        if (request.Mode == ArtworkMode.Pinned)
        {
            if ((request.AssetId is null) == (request.MediaId is null))
                failures.TryAdd("assetId", ["A pinned role names exactly one of assetId or mediaId."]);
            foreach (var (field, value) in new[] { ("focalX", request.FocalX), ("focalY", request.FocalY) })
                if (value is < 0 or > 100)
                    failures.TryAdd(field, ["A focal point is a percentage from 0 to 100."]);
            return;
        }

        foreach (var (field, supplied) in new[]
                 {
                     ("assetId", request.AssetId is not null), ("mediaId", request.MediaId is not null),
                     ("focalX", request.FocalX is not null), ("focalY", request.FocalY is not null)
                 })
            if (supplied)
                failures[field] = [$"Only a pinned role takes {field}."];
    }

    private static async Task<IResult> Apply(Sqid titleId, string role, ApplyArtworkRequest request, HttpContext context,
        ICurrentUser user, ITitleReader titles, IArtworkBrowsingService browsing, ArtworkCurationService curation,
        CancellationToken ct)
    {
        if (ParseRole(role) is not { } parsed)
            return UnknownRole();
        if (user.UserId is not { } actor)
            return ActorRequired();
        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        if (!await TitleExistsAsync(titles, titleId, ct))
            return TitleEndpoints.TitleNotFound();
        var validated = await browsing.ValidateCandidateAsync(titleId.Value, actor, request.CandidateReference, ct);
        if (validated.IsError)
            return validated.Errors.ToProblem();
        var candidate = validated.Value;
        if (candidate.Role != parsed)
            return ProblemResults.ValidationProblem("Artwork.InvalidRequest", "candidateReference",
                "The candidate is for another artwork role.");
        var accepted = await curation.RequestAsync(new ArtworkImportRequest(request.RequestId, titleId.Value,
            parsed, candidate.ProviderId, candidate.GameId, candidate.AssetId,
            candidate.TrustedAssetUrl, candidate.Attribution, actor, ifMatch.Value, request.FocalX, request.FocalY), ct);
        return accepted.Match(job => JobAccepted.Result(job.Id), errors => errors.ToProblem());
    }

    /// <summary>The saved assets, with the family validator read first, so a write between the reads makes it stale.</summary>
    private static async Task<IResult> ListAssets(Sqid titleId, HttpContext context, IArtworkCurationRepository repository,
        CancellationToken ct, string? cursor = null, [Range(1, 100)] int limit = 50)
    {
        var family = await repository.GetFamilyAsync(titleId.Value, ct);
        if (family.IsError)
            return family.Errors.ToProblem();
        var result = await repository.ListAssetsAsync(titleId.Value, cursor, limit, ct);
        if (result.IsError)
            return result.Errors.ToProblem();
        return Results.Ok(new Page<SavedArtworkDto>
        {
            Items = result.Value.Items.Select(item =>
            {
                var asset = item.Asset;
                return new SavedArtworkDto(IdCoder.Encode(asset.Id),
                asset.Role.ToString(), asset.SourceId, asset.Attribution, asset.SourcePageUrl,
                item.MediaIds.Select(IdCoder.Encode).ToArray(),
                new ArtworkResolution(asset.Role, asset, null, ArtworkFit.Cover, ArtworkFallbackReason.None).ToContract());
            }).ToArray(),
            NextCursor = result.Value.NextCursor,
            HasNextPage = result.Value.HasNextPage
        })
            .WithETag(context, family.Value.Revision);
    }

    private static Task<IResult> DeleteAsset(Sqid titleId, Sqid assetId, HttpContext context, ArtworkCurationService service,
        CancellationToken ct) =>
        FamilyWriteAsync(context, ifMatch => service.DeleteAsync(titleId.Value, assetId.Value, ifMatch, ct));

    /// <summary>The rejected assets, with the family validator read first, so a write between the reads makes it stale.</summary>
    private static async Task<IResult> ListRejections(Sqid titleId, HttpContext context, IArtworkCurationRepository repository,
        CancellationToken ct, string? cursor = null, [Range(1, 100)] int limit = 50)
    {
        var family = await repository.GetFamilyAsync(titleId.Value, ct);
        if (family.IsError)
            return family.Errors.ToProblem();
        var result = await repository.ListRejectionsAsync(titleId.Value, cursor, limit, ct);
        return result.Match(page => Results.Ok(new Page<RejectedArtworkDto>
        {
            Items = page.Items.Select(x => new RejectedArtworkDto(IdCoder.Encode(x.Id), x.Role.ToString(), x.SourceId, x.ProviderAssetId)).ToArray(),
            NextCursor = page.NextCursor,
            HasNextPage = page.HasNextPage
        })
                .WithETag(context, family.Value.Revision),
            errors => errors.ToProblem());
    }

    private static Task<IResult> Reject(Sqid titleId, Sqid assetId, HttpContext context, ArtworkCurationService service,
        CancellationToken ct) =>
        FamilyWriteAsync(context, ifMatch => service.RejectAsync(titleId.Value, assetId.Value, true, ifMatch, ct));

    private static Task<IResult> Restore(Sqid titleId, Sqid assetId, HttpContext context, ArtworkCurationService service,
        CancellationToken ct) =>
        FamilyWriteAsync(context, ifMatch => service.RejectAsync(titleId.Value, assetId.Value, false, ifMatch, ct));

    private static async Task<IResult> Hide(Sqid titleId, string candidateId, HideArtworkCandidateRequest request,
        HttpContext context, ICurrentUser user, ITitleReader titles, IArtworkBrowsingService browsing,
        ArtworkCurationService service, CancellationToken ct)
    {
        if (user.UserId is not { } actor)
            return ActorRequired();
        return await FamilyWriteAsync(context, async ifMatch =>
        {
            if (!await TitleExistsAsync(titles, titleId, ct))
                return CatalogErrors.TitleNotFound();
            var candidate = await browsing.ValidateCandidateAsync(titleId.Value, actor, request.CandidateReference, ct);
            if (candidate.IsError)
                return candidate.Errors;
            return ArtworkCandidateIds.Of(candidate.Value.TrustedAssetUrl) == candidateId
                ? await service.HideAsync(titleId.Value, candidate.Value.TrustedAssetUrl, ifMatch, ct)
                : ArtworkCurationErrors.CandidateMismatch();
        });
    }

    private static async Task<IResult> Browse(Sqid titleId, string providerId, string gameId, ArtworkRole role,
        ICurrentUser user, ITitleReader titles, IArtworkBrowsingService service, CancellationToken ct,
        string? dimension = null, string? style = null, string? cursor = null, MediaType? mediaType = null)
    {
        if (user.UserId is not { } actor)
            return ActorRequired();
        if (!await TitleExistsAsync(titles, titleId, ct))
            return TitleEndpoints.TitleNotFound();
        var result = await service.BrowseAsync(titleId.Value, actor, providerId, gameId,
            new ProviderArtworkQuery(role, dimension, style, mediaType), cursor, ct);
        return result.Match(page => Results.Ok(new Page<ArtworkCandidateDto>
        {
            Items = page.Items.Select(candidate =>
            new ArtworkCandidateDto(candidate.Reference, candidate.CandidateId, candidate.ProviderAssetId, candidate.Role.ToString(),
                candidate.Width, candidate.Height, candidate.Style, candidate.Attribution, candidate.SourcePageUrl, candidate.MediaType?.ToString(), candidate.IsSaved)).ToArray(),
            NextCursor = page.NextCursor,
            HasNextPage = page.NextCursor is not null
        }), errors => errors.ToProblem());
    }

    private static Task<IResult> ReplaceSteamGridSettings(UpdateSteamGridDbSettingsRequest request, HttpContext context,
        ICommandHandler<ReplaceSteamGridDbSettingsCommand, SteamGridDbSettingsDto> handler, CancellationToken ct) =>
        SteamGridWrite(context, precondition => handler.HandleAsync(new(request, precondition), ct));

    private static Task<IResult> TestSteamGridConnection(HttpContext context,
        ICommandHandler<TestSteamGridDbConnectionCommand, SteamGridDbSettingsDto> handler, CancellationToken ct) =>
        SteamGridWrite(context, precondition => handler.HandleAsync(new(precondition), ct));

    private static async Task<IResult> SteamGridWrite(HttpContext context,
        Func<Romd.Application.Common.Concurrency.RevisionPrecondition, Task<ErrorOr<SteamGridDbSettingsDto>>> write)
    {
        var precondition = Preconditions.IfMatch(context, "SteamGridDb");
        if (precondition.IsError)
            return precondition.Errors.ToProblem();
        return (await write(precondition.Value)).Match(state => Results.Ok(state).WithETag(context, state.Revision.ToString()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> Preview(Sqid titleId, PreviewArtworkRequest request,
        HttpContext context, ICurrentUser user, ITitleReader titles, IArtworkBrowsingService service, CancellationToken ct)
    {
        if (user.UserId is not { } actor)
            return ActorRequired();
        if (!await TitleExistsAsync(titles, titleId, ct))
            return TitleEndpoints.TitleNotFound();
        var result = await service.PreviewAsync(titleId.Value, actor, request.CandidateReference, ct);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return result.Match(image => Results.File(image.Bytes, image.ContentType), errors => errors.ToProblem());
    }

    private static async Task<IResult> ImportGallery(Sqid titleId, string candidateId, ImportGalleryArtworkRequest request,
        HttpContext context, ICurrentUser user, GalleryArtworkService service, CancellationToken ct)
    {
        if (user.UserId is not { } actor)
            return ActorRequired();
        var ifMatch = Preconditions.IfMatch(context, TitleEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await service.ImportAsync(titleId.Value, actor, candidateId, request.CandidateReference,
            ifMatch.Value, ct);
        // A new item is 201 at its own URL; a repeat of the same content under the current ETag is the 200 no-op.
        return result.Match(
            imported => (imported.Created
                    ? Results.Created($"/api/titles/{titleId}/media/{imported.Media.Id}", imported.Media)
                    : Results.Ok(imported.Media))
                .WithETag(context, imported.TitleRevision),
            errors => errors.ToProblem());
    }

    /// <summary>Runs a family write under the request's <c>If-Match</c> and answers the family with its new validator.</summary>
    private static async Task<IResult> FamilyWriteAsync(HttpContext context,
        Func<Romd.Application.Common.Concurrency.RevisionPrecondition, Task<ErrorOr<ArtworkFamily>>> write)
    {
        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        return ifMatch.IsError
            ? ifMatch.Errors.ToProblem()
            : (await write(ifMatch.Value)).Match(family => FamilyTagged(context, family), errors => errors.ToProblem());
    }

    private static IResult FamilyTagged(HttpContext context, ArtworkFamily family) =>
        Results.Ok(new TitleArtworkDto(family.Revision, family.Roles.Select(ToContract).ToArray()))
            .WithETag(context, family.Revision);

    private static IResult RoleTagged(HttpContext context, ArtworkCurationState state) =>
        Results.Ok(ToContract(state)).WithETag(context, ArtworkValidators.Role(state.Revision));

    private static ArtworkSelectionStateDto ToContract(ArtworkCurationState state) => new(state.Role.ToString(),
        state.Mode switch
        {
            ArtworkSelectionMode.Automatic => ArtworkMode.Automatic,
            ArtworkSelectionMode.Empty => ArtworkMode.Empty,
            ArtworkSelectionMode.Pinned => ArtworkMode.Pinned,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Mode, "Not an artwork selection mode.")
        }, state.Revision, state.PinnedAssetId is { } id ? IdCoder.Encode(id) : null, state.PendingJobId);

    /// <summary>
    ///     An unknown title answers 404 before any candidate or provider work, so a reference bound to another title
    ///     never decides the status (docs/decisions/api-contract-policy.md, "Resource Model").
    /// </summary>
    private static async Task<bool> TitleExistsAsync(ITitleReader titles, Sqid titleId, CancellationToken ct) =>
        await titles.GetSummaryAsync(titleId.Value, ct) is not null;

    private static IResult ActorRequired() => new[] { ArtworkCurationErrors.ActorRequired() }.ToProblem();

    private static IResult UnknownRole() => new[] { ArtworkCurationErrors.UnknownRole() }.ToProblem();

    /// <summary>Publishes the role names as the only values of the <c>{role}</c> segment, which <see cref="ParseRole" /> matches.</summary>
    private static RouteHandlerBuilder WithRoleValues(this RouteHandlerBuilder builder) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>()
                         .Where(parameter => parameter.In == ParameterLocation.Path && parameter.Name == "role") ?? [])
                parameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = Enum.GetNames<ArtworkRole>().Select(name => (JsonNode)JsonValue.Create(name)!).ToList()
                };
            return Task.CompletedTask;
        });

    /// <summary>An artwork role, matched exactly as the contract names it; enum parsing would also accept numbers.</summary>
    private static ArtworkRole? ParseRole(string value) =>
        Enum.GetNames<ArtworkRole>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<ArtworkRole>(value) : null;
}
