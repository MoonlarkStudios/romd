using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy;
using Romd.Admin.Application.Source.Platform.Queries.GetMetadataPolicy;
using Romd.Contracts.Management.Enrichment;
using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;
using Romd.Contracts.Management.Artwork;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

public static class EnrichmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrichmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/enrichment")
            .WithTags("Enrichment");

        group.MapGet("artwork/settings", GetArtworkSettings).WithName("GetArtworkEnrichmentSettings")
            .Produces<ArtworkEnrichmentSettingsDto>().RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapPut("artwork/settings", UpdateArtworkSettings).WithName("UpdateArtworkEnrichmentSettings")
            .Produces<ArtworkEnrichmentSettingsDto>().ProducesProblem(409).RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("artwork/{titleId}", GetArtworkOutcomes).WithName("GetArtworkAcquisitionOutcomes")
            .Produces<ArtworkAcquisitionStateDto>().Produces(404).RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapPost("artwork/{titleId}", FillMissingArtwork).WithName("FillMissingTitleArtwork")
            .Produces(202).ProducesProblem(404).RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("stats", GetStats)
            .WithName("GetEnrichmentStats")
            .WithDescription("Get enrichment statistics across all titles")
            .Produces<EnrichmentStatsResponse>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        // Title enrichment state
        var titleGroup = app.MapGroup("/titles")
            .WithTags("Titles");

        titleGroup.MapGet("{titleId}/enrichment", GetTitleEnrichmentState)
            .WithName("GetTitleEnrichmentState")
            .WithDescription("Get full enrichment state for a title including all provider data per field")
            .Produces<TitleEnrichmentStateResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        titleGroup.MapPatch("{titleId}/enrichment/fields", SetFieldOverrides)
            .WithName("SetFieldOverrides")
            .WithDescription("Set or clear per-field source overrides for a title")
            .Produces<Models.TitleDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Platform enrichment defaults
        var platformGroup = app.MapGroup("/systems")
            .WithTags("Platforms");

        platformGroup.MapGet("{systemKey}/enrichment/defaults", GetPlatformFieldDefaults)
            .WithName("GetPlatformFieldDefaults")
            .Produces<PlatformMetadataPolicyDto>()
            .Produces(404)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        platformGroup.MapPatch("{systemKey}/enrichment/defaults", SetPlatformFieldDefaults)
            .WithName("SetPlatformFieldDefaults")
            .WithDescription("Set or clear platform-level field defaults for metadata resolution")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        platformGroup.MapPost("{systemKey}/enrichment/bulk", TriggerBulkEnrichment)
            .WithName("TriggerBulkEnrichment")
            .WithDescription("Trigger bulk enrichment for a platform; scope=Tracked (default) or All")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<ArtworkEnrichmentSettingsDto> GetArtworkSettings(IArtworkAcquisitionStore store,
        IEnumerable<IProviderIdentityAdapter> adapters, CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(ct);
        var enabled = new List<string>();
        foreach (var adapter in adapters.Where(x => x.Capabilities.Contains("artwork")))
        {
            var state = await adapter.GetAvailabilityAsync(ct);
            if (state.Enabled && state.Configured) enabled.Add(adapter.Id);
        }
        return settings with { Providers = enabled };
    }

    private static async Task<IResult> UpdateArtworkSettings(UpdateArtworkEnrichmentSettingsRequest request,
        IArtworkAcquisitionStore store, IEnumerable<IProviderIdentityAdapter> adapters, CancellationToken ct) =>
        await store.UpdateSettingsAsync(request, ct) ? Results.Ok(await GetArtworkSettings(store, adapters, ct)) :
            Results.Problem(statusCode: 409, detail: "Enrichment settings changed. Reload before saving.");

    private static async Task<IResult> GetArtworkOutcomes(Sqid titleId, ITitleRepository titles,
        IArtworkAcquisitionStore store, CancellationToken ct) =>
        await titles.GetWithMetadataLayersAsync(titleId, ct) is null ? Results.NotFound() :
            Results.Ok(await store.GetOutcomesAsync(titleId, ct));

    private static async Task<IResult> FillMissingArtwork(Sqid titleId,
        ICommandHandler<TriggerTitleEnrichmentCommand, Guid> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(titleId, ArtworkOnly: true), ct);
        return result.Match(_ => Results.Accepted(), errors => errors.ToProblem());
    }

    private static async Task<IResult> GetStats(
        ITitleRepository titleRepository,
        CancellationToken ct)
    {
        var stats = await titleRepository.GetEnrichmentStatsAsync(ct);

        return Results.Ok(new EnrichmentStatsResponse
        {
            Pending = stats.Pending,
            Completed = stats.Completed,
            NotFound = stats.NotFound,
            Failed = stats.Failed,
            LowConfidence = stats.LowConfidence,
            Total = stats.Pending + stats.Completed + stats.NotFound + stats.Failed + stats.LowConfidence
        });
    }

    private static async Task<IResult> GetTitleEnrichmentState(
        Sqid titleId,
        ITitleRepository titleRepository,
        CancellationToken ct)
    {
        var title = await titleRepository.GetWithMetadataLayersAsync(titleId, ct);
        if (title is null)
        {
            return Results.NotFound();
        }

        var layers = title.MetadataLayers.Select(l =>
        {
            var payload = l.GetPayload();
            return new MetadataLayerDto
            {
                SourceId = l.SourceId,
                SourceType = l.SourceType.ToString(),
                Description = payload?.Description,
                Publisher = payload?.Publisher,
                Developer = payload?.Developer,
                Genre = payload?.Genre,
                ReleaseDate = payload?.ReleaseDate,
                Players = payload?.Players,
                Rating = payload?.Rating,
                UpdatedAt = l.UpdatedAt
            };
        }).ToList();

        var externalIds = title.ExternalIds.Select(e => new ExternalIdDto
        {
            Provider = e.Provider,
            ExternalId = e.ExternalId,
            MatchConfidence = e.MatchConfidence,
            IsConfirmed = e.IsConfirmed,
            CreatedAt = e.CreatedAt
        }).ToList();

        return Results.Ok(new TitleEnrichmentStateResponse
        {
            TitleId = IdCoder.Encode(title.Id),
            Name = title.Name,
            EnrichmentStatus = title.EnrichmentStatus.ToString(),
            FieldProvenance = title.FieldProvenance?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            FieldSourceOverrides = title.FieldSourceOverrides?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Layers = layers,
            ExternalIds = externalIds,
            LastEnrichedAt = title.LastEnrichedAt
        });
    }

    private static async Task<IResult> SetFieldOverrides(
        Sqid titleId,
        SetFieldOverridesRequest request,
        ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new SetFieldOverridesCommand(titleId, request.Overrides),
            ct);
        return result.Match(
            detail => Results.Ok(detail),
            errors => errors.Count > 0 && errors[0].Type == ErrorType.NotFound
                ? Results.NotFound()
                : errors.ToProblem());
    }

    private static async Task<IResult> GetPlatformFieldDefaults(string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetMetadataPolicyQuery, PlatformMetadataPolicyDto> handler, CancellationToken ct)
    {
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, ct) ?? 0;
        if (platformId == 0) return Results.NotFound();
        var result = await handler.HandleAsync(new(platformId), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> SetPlatformFieldDefaults(string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        SetPlatformFieldDefaultsRequest request,
        ICommandHandler<SetMetadataPolicyCommand, Success> handler, CancellationToken ct)
    {
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, ct) ?? 0;
        if (platformId == 0) return Results.NotFound();
        var result = await handler.HandleAsync(new(platformId, request.Revision, request.Defaults), ct);
        return result.Match(_ => Results.Accepted(), errors => errors.ToProblem());
    }
    private static async Task<IResult> TriggerBulkEnrichment(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        HttpContext httpContext,
        IPlatformRepository platformRepository,
        IEnrichmentScheduler enrichmentScheduler,
        CancellationToken ct,
        EnrichmentScope scope = EnrichmentScope.Tracked)
    {
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, ct) ?? 0;
        if (platformId == 0) return Results.NotFound();
        // Enum binding via Enum.TryParse accepts numeric strings ("0", "5"), which would leak
        // integer identity for enum values (docs/decisions/admin-api-contract-policy.md,
        // "Strict Validation"). Only named values are valid; unknown names already fail binding
        // with the shared 400 envelope before this handler runs.
        if (InvalidScopeProblem(httpContext) is { } scopeProblem)
        {
            return scopeProblem;
        }

        var platform = await platformRepository.GetByIdAsync(platformId, ct);
        if (platform is null)
        {
            return Results.NotFound();
        }

        await enrichmentScheduler.EnqueueBulkAsync(platform.Id, scope, ct: ct);

        return Results.Accepted();
    }

    /// <summary>
    ///     Rejects any supplied <c>scope</c> query value that is not a named
    ///     <see cref="EnrichmentScope" /> member. An absent value stays valid (default scope).
    /// </summary>
    private static IResult? InvalidScopeProblem(HttpContext httpContext)
    {
        if (!httpContext.Request.Query.TryGetValue("scope", out var rawValues))
        {
            return null;
        }

        var invalidValues = rawValues
            .Where(raw => !Enum.GetNames<EnrichmentScope>()
                .Any(name => string.Equals(name, raw, StringComparison.OrdinalIgnoreCase)))
            .Select(raw => $"'{raw}' is not a valid enrichment scope. Valid values: {string.Join(", ", Enum.GetNames<EnrichmentScope>())}.")
            .ToArray();

        // Same errorCode as a binding failure: numeric strings and unknown names are both
        // request-shape failures for this parameter; only the detection point differs.
        return invalidValues.Length > 0
            ? ProblemResults.ValidationProblem(
                "Request.InvalidBody",
                new Dictionary<string, string[]> { ["scope"] = invalidValues })
            : null;
    }
}

public sealed record EnrichmentStatsResponse
{
    public int Pending { get; init; }
    public int Completed { get; init; }
    public int NotFound { get; init; }
    public int Failed { get; init; }
    public int LowConfidence { get; init; }
    public int Total { get; init; }
}

public sealed record TitleEnrichmentStateResponse
{
    public required string TitleId { get; init; }
    public required string Name { get; init; }
    public required string EnrichmentStatus { get; init; }
    public Dictionary<string, string>? FieldProvenance { get; init; }
    public Dictionary<string, string>? FieldSourceOverrides { get; init; }
    public required List<MetadataLayerDto> Layers { get; init; }
    public required List<ExternalIdDto> ExternalIds { get; init; }
    public DateTimeOffset? LastEnrichedAt { get; init; }
}

public sealed record MetadataLayerDto
{
    public required string SourceId { get; init; }
    public required string SourceType { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ExternalIdDto
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public float MatchConfidence { get; init; }
    public bool IsConfirmed { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record SetFieldOverridesRequest
{
    public required Dictionary<string, string?> Overrides { get; init; }
}

public sealed record SetPlatformFieldDefaultsRequest
{
    public required string Revision { get; init; }
    public required Dictionary<string, string?> Defaults { get; init; }
}
