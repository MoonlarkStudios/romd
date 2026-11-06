using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Queries.GetTitleEnrichmentState;

public sealed record GetTitleEnrichmentStateQuery(int TitleId) : IQuery<TitleEnrichmentStateResponse>;

/// <summary>Reads a complete title snapshot; sustained revision churn is a retryable read conflict.</summary>
public sealed class GetTitleEnrichmentStateQueryHandler
    : IQueryHandler<GetTitleEnrichmentStateQuery, TitleEnrichmentStateResponse>
{
    private readonly ITitleAggregateReader _titles;

    public GetTitleEnrichmentStateQueryHandler(ITitleAggregateReader titles)
    {
        _titles = titles;
    }

    public async Task<ErrorOr<TitleEnrichmentStateResponse>> HandleAsync(
        GetTitleEnrichmentStateQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Title? title;
        try
        {
            title = await _titles.GetAggregateAsync(query.TitleId, ct);
        }
        catch (PersistenceConflictException)
        {
            ct.ThrowIfCancellationRequested();
            return CatalogErrors.TitleReadConflict();
        }
        if (title is null)
            return CatalogErrors.TitleNotFound();

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

        return new TitleEnrichmentStateResponse
        {
            TitleId = IdCoder.Encode(title.Id),
            Revision = title.Revision.ToString(),
            Name = title.Name,
            EnrichmentStatus = title.EnrichmentStatus.ToString(),
            FieldProvenance = title.FieldProvenance?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            FieldSourceOverrides = title.FieldSourceOverrides?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Layers = layers,
            ExternalIds = externalIds,
            LastEnrichedAt = title.LastEnrichedAt
        };
    }
}
