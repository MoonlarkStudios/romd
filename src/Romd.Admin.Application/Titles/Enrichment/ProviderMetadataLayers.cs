using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Records an accepted provider result as that provider's metadata layer. Identity acceptance and
///     association writes belong to the shared provider identity service, so this never changes external
///     IDs. Storing a layer does not rematerialize: the domain cascade decides effective fields later,
///     which keeps user values, field-source overrides and <see cref="Title.Name" /> authoritative.
/// </summary>
public static class ProviderMetadataLayers
{
    public static void Store(Title title, string providerId, EnrichmentResult result, DateTimeOffset updatedAt)
    {
        if (result.Data?.HasAnyData() != true)
            return;

        title.StoreProviderLayer(providerId, MetadataSourceType.Provider,
            ToPayload(result.Data) with { ProviderGameId = result.ExternalId }, updatedAt);
    }

    private static TitleMetadataPayload ToPayload(EnrichmentData data) => new()
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
