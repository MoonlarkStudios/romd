using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Coordinates layer storage and rematerialization.
///     Converts EnrichmentData → TitleMetadataPayload and stores it as a layer on the title.
/// </summary>
public sealed class MetadataMerger(
    IPlatformFieldDefaultRepository platformFieldDefaultRepo,
    IOptions<EnrichmentOptions> enrichmentOptions) : IMetadataRematerializer
{
    private readonly EnrichmentOptions _options = enrichmentOptions.Value;

    /// <summary>
    ///     Stores a provider's enrichment result as a metadata layer on the title.
    ///     Also sets the external ID if one was returned.
    /// </summary>
    public void StoreProviderResult(Title title, string providerId, EnrichmentResult result)
    {
        if (result.ExternalId != null)
        {
            title.SetExternalIdFromAutoEnrichment(providerId, result.ExternalId, result.MatchConfidence);
            if (title.GetExternalId(providerId)?.ExternalId != result.ExternalId) return;
        }

        if (result.Data?.HasAnyData() == true)
        {
            var payload = MapToPayload(result.Data);
            title.StoreProviderLayer(providerId, MetadataSourceType.Provider, payload);
        }
    }

    /// <summary>
    ///     Rematerializes all effective fields on the title from layers.
    ///     Loads platform defaults and applies the full cascade.
    /// </summary>
    public async Task RematerializeAsync(Title title, CancellationToken ct = default)
    {
        var platformDefaults = await platformFieldDefaultRepo.GetByPlatformIdAsync(title.PlatformId, ct);
        title.Rematerialize(_options.GlobalSourcePriority, platformDefaults);
        title.RecalculatePrimaryMedia(_options.GlobalSourcePriority);
    }

    private static TitleMetadataPayload MapToPayload(EnrichmentData data)
    {
        return new TitleMetadataPayload
        {
            Description = data.Description,
            Publisher = data.Publisher,
            Developer = data.Developer,
            Genre = data.Genre,
            ReleaseDate = data.ReleaseDate,
            Players = data.Players,
            Rating = data.Rating,
            ContentRatings = data.ContentRatings
        };
    }
}
