using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ArtworkCurationEndpoints
{
    public static IEndpointRouteBuilder MapArtworkCurationEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/admin/artwork-providers/steamgriddb")
            .WithTags("ArtworkSettings").RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        settings.MapGet("", async (ISteamGridDbSettingsService service, CancellationToken ct) =>
                Results.Ok(await service.GetAsync(ct)))
            .WithName("GetSteamGridDbSettings").Produces<SteamGridDbSettingsDto>();
        settings.MapPut("", async (UpdateSteamGridDbSettingsRequest request, ISteamGridDbSettingsService service, CancellationToken ct) =>
                (await service.UpdateAsync(request, ct)).Match(Results.Ok, errors => errors.ToProblem()))
            .WithName("UpdateSteamGridDbSettings").Produces<SteamGridDbSettingsDto>()
            .Produces<ProblemDetails>(400).Produces<ProblemDetails>(409);
        settings.MapPost("/test-connection", async (ISteamGridDbSettingsService service, CancellationToken ct) =>
                Results.Ok(await service.TestConnectionAsync(ct)))
            .WithName("TestSteamGridDbConnection").Produces<SteamGridDbSettingsDto>();

        var group = app.MapGroup("/artwork").WithTags("Artwork")
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapGet("/providers", Providers).WithName("GetArtworkProviders").Produces<IReadOnlyList<ArtworkProviderDto>>();
        group.MapGet("/titles/{titleId}/selections", Selections).WithName("GetArtworkSelections")
            .Produces<IReadOnlyList<ArtworkSelectionStateDto>>().Produces<ProblemDetails>(404);
        group.MapGet("/titles/{titleId}/saved", Saved).WithName("GetSavedArtwork")
            .Produces<IReadOnlyList<SavedArtworkDto>>().Produces<ProblemDetails>(404);
        group.MapPost("/titles/{titleId}/roles/{role}/pin", Pin).WithName("PinSavedArtwork")
            .Produces<ArtworkAutomaticDto>().Produces<ProblemDetails>(400).Produces<ProblemDetails>(404).Produces<ProblemDetails>(409);
        group.MapGet("/titles/{titleId}/games", Search).WithName("SearchArtworkGames")
            .Produces<IReadOnlyList<ArtworkProviderGameDto>>().Produces<ProblemDetails>(400);
        group.MapGet("/titles/{titleId}/candidates", Browse).WithName("BrowseArtworkCandidates")
            .Produces<ArtworkCandidatePageDto>().Produces<ProblemDetails>(400);
        group.MapPost("/titles/{titleId}/preview", Preview).WithName("PreviewArtworkCandidate")
            .Produces(200, contentType: "image/png").Produces<ProblemDetails>(400);
        group.MapPost("/titles/{titleId}/gallery", ImportGallery).WithName("ImportGalleryArtwork")
            .Produces<Romd.Contracts.Management.Models.TitleMediaRef>().Produces<ProblemDetails>(400)
            .Produces<ProblemDetails>(404).Produces<ProblemDetails>(409);
        group.MapPost("/titles/{titleId}/apply", Apply).WithName("ApplyArtworkCandidate")
            .Produces<ArtworkImportAcceptedDto>(202).Produces<ProblemDetails>(400)
            .Produces<ProblemDetails>(404).Produces<ProblemDetails>(409);
        group.MapPost("/titles/{titleId}/roles/{role}/automatic", Automatic).WithName("ReturnArtworkToAutomatic")
            .Produces<ArtworkAutomaticDto>().Produces<ProblemDetails>(400).Produces<ProblemDetails>(404);
        return app;
    }

    private static async Task<IResult> Providers(IArtworkBrowsingService service, ITitleProviderMatchService matches,
        CancellationToken ct, Sqid? titleId = null, string? role = null)
    {
        var states = await service.GetCapabilitiesAsync(ct);
        if (titleId is { } title)
        {
            if (role is not null && !TryRole(role, out _)) return new[] { ArtworkCurationErrors.InvalidRequest() }.ToProblem();
            var links = await matches.GetAsync(title.Value, ct);
            if (links.IsError) return links.Errors.ToProblem();
            var available = new List<ArtworkProviderDto>();
            foreach (var state in states.Where(state => state.IsAvailable))
            {
                var link = links.Value.SingleOrDefault(link => link.ProviderId == state.Provider.ProviderId && link.IsAvailable);
                if (link?.ExternalId is not { } gameId ||
                    await matches.GetLinkRevisionAsync(title.Value, link.ProviderId, gameId, ct) is null) continue;
                var roles = state.Provider.Roles.Where(item => role is null || item.Role.ToString().Equals(role, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (roles.Length == 0) continue;
                available.Add(new(state.Provider.ProviderId, state.Provider.Name, true, state.Provider.SupportsLanguageFilter,
                    roles.Select(item => new ArtworkRoleCapabilityDto(item.Role.ToString(), item.Dimensions, item.Styles)).ToArray(),
                    gameId, link.Game?.Name, link.Game?.Url, state.Provider.MediaTypes?.Select(type => type.ToString()).ToArray()));
            }
            return Results.Ok(available);
        }
        return Results.Ok(states.Select(state => new ArtworkProviderDto(state.Provider.ProviderId, state.Provider.Name, state.IsAvailable,
            state.Provider.SupportsLanguageFilter, state.Provider.Roles.Select(role => new ArtworkRoleCapabilityDto(
                role.Role.ToString(), role.Dimensions, role.Styles)).ToArray(),
            MediaTypes: state.Provider.MediaTypes?.Select(type => type.ToString()).ToArray())).ToArray());
    }

    private static async Task<IResult> Selections(Sqid titleId, IArtworkCurationRepository repository, CancellationToken ct)
    {
        var result = await repository.GetStatesAsync(titleId.Value, ct);
        return result.Match(states => Results.Ok(states.Select(state => new ArtworkSelectionStateDto(
            state.Role.ToString(), state.Mode.ToString(), state.Revision,
            state.PinnedAssetId is { } id ? IdCoder.Encode(id) : null, state.PendingJobId)).ToArray()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> Saved(Sqid titleId, IArtworkCurationRepository repository,
        Romd.Admin.Application.Titles.ITitleRepository titles, CancellationToken ct)
    {
        var result = await repository.GetGalleryAsync(titleId.Value, ct);
        if (result.IsError) return result.Errors.ToProblem();
        var title = await titles.GetWithCollectionsAsync(titleId.Value, ct);
        return result.Match(assets => Results.Ok(assets.Select(asset => new SavedArtworkDto(IdCoder.Encode(asset.Id),
            asset.Role.ToString(), asset.SourceId, asset.Attribution, asset.SourcePageUrl,
            title?.Media.Where(media => media.FileId == asset.Original.FileId).Select(media => IdCoder.Encode(media.Id)).ToArray() ?? [],
            new ArtworkResolution(asset.Role, asset, null, ArtworkFit.Cover, ArtworkFallbackReason.None).ToContract())).ToArray()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> Pin(Sqid titleId, string role, PinSavedArtworkRequest request,
        ICurrentUser user, ArtworkCurationService service, ILocalArtworkService local, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Results.Unauthorized();
        if (!TryRole(role, out var parsed) || (request.AssetId is null) == (request.MediaId is null))
            return new[] { ArtworkCurationErrors.InvalidRequest() }.ToProblem();
        if (!Sqid.TryParse(request.AssetId ?? request.MediaId, null, out var id))
            return new[] { ArtworkCurationErrors.InvalidRequest() }.ToProblem();
        var result = request.AssetId is not null
            ? await service.PinAsync(titleId.Value, parsed, id.Value, request.ExpectedRevision, ct, request.FocalX, request.FocalY)
            : await local.PinMediaAsync(titleId.Value, parsed, id.Value, request.ExpectedRevision, actor, ct, request.FocalX, request.FocalY);
        return result.Match(revision => Results.Ok(new ArtworkAutomaticDto(revision)), errors => errors.ToProblem());
    }

    private static async Task<IResult> Search(Sqid titleId, string providerId, string query,
        ICurrentUser user, IArtworkBrowsingService service, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Results.Unauthorized();
        var result = await service.SearchAsync(titleId.Value, actor, providerId, query, ct);
        return result.Match(games => Results.Ok(games.Select(game => new ArtworkProviderGameDto(game.Id, game.Name)).ToArray()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> Browse(Sqid titleId, string providerId, string gameId, string role,
        ICurrentUser user, IArtworkBrowsingService service, CancellationToken ct,
        string? dimension = null, string? style = null, string? cursor = null, string? mediaType = null)
    {
        if (user.UserId is not { } actor) return Results.Unauthorized();
        if (!TryRole(role, out var parsed)) return new[] { ArtworkCurationErrors.InvalidRequest() }.ToProblem();
        MediaType? type = null;
        if (mediaType is not null)
        {
            if (!Enum.TryParse<MediaType>(mediaType, true, out var parsedType) || !Enum.IsDefined(parsedType) || int.TryParse(mediaType, out _))
                return new[] { ArtworkCurationErrors.InvalidRequest() }.ToProblem();
            type = parsedType;
        }
        var result = await service.BrowseAsync(titleId.Value, actor, providerId, gameId,
            new ProviderArtworkQuery(parsed, dimension, style, type), cursor, ct);
        return result.Match(page => Results.Ok(new ArtworkCandidatePageDto(page.Items.Select(candidate =>
            new ArtworkCandidateDto(candidate.Reference, candidate.ProviderAssetId, candidate.Role.ToString(),
                candidate.Width, candidate.Height, candidate.Style, candidate.Attribution, candidate.SourcePageUrl, candidate.MediaType?.ToString(), candidate.IsSaved)).ToArray(),
            page.NextCursor)), errors => errors.ToProblem());
    }

    private static async Task<IResult> Preview(Sqid titleId, PreviewArtworkRequest request,
        HttpContext context, ICurrentUser user, IArtworkBrowsingService service, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Results.Unauthorized();
        var result = await service.PreviewAsync(titleId.Value, actor, request.CandidateReference, ct);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return result.Match(image => Results.File(image.Bytes, image.ContentType), errors => errors.ToProblem());
    }

    private static async Task<IResult> ImportGallery(Sqid titleId, ImportGalleryArtworkRequest request,
        ICurrentUser user, GalleryArtworkService service, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Results.Unauthorized();
        var result = await service.ImportAsync(titleId.Value, actor, request.CandidateReference, ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> Apply(Sqid titleId, ApplyArtworkRequest request,
        ICurrentUser user, IArtworkBrowsingService browsing, ArtworkCurationService curation, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Results.Unauthorized();
        var validated = await browsing.ValidateCandidateAsync(titleId.Value, actor, request.CandidateReference, ct);
        if (validated.IsError) return validated.Errors.ToProblem();
        var candidate = validated.Value;
        var accepted = await curation.RequestAsync(new ArtworkImportRequest(request.RequestId, titleId.Value,
            candidate.Role, candidate.ProviderId, candidate.GameId, candidate.AssetId,
            candidate.TrustedAssetUrl, candidate.Attribution, actor, request.FocalX, request.FocalY, request.ExpectedRevision), ct);
        return accepted.Match(job => Results.Accepted($"/api/jobs/{job.Id}",
            new ArtworkImportAcceptedDto(job.Id, job.SelectionRevision)), errors => errors.ToProblem());
    }

    private static async Task<IResult> Automatic(Sqid titleId, string role, ArtworkCurationService service, CancellationToken ct)
    {
        if (!TryRole(role, out var parsed)) return new[] { ArtworkCurationErrors.InvalidRequest() }.ToProblem();
        var result = await service.ReturnToAutomaticAsync(titleId.Value, parsed, ct);
        return result.Match(revision => Results.Ok(new ArtworkAutomaticDto(revision)), errors => errors.ToProblem());
    }

    private static bool TryRole(string value, out ArtworkRole role) =>
        Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role) &&
        !int.TryParse(value, out _);
}
