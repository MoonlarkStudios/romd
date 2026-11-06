using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Applies the domain metadata cascade to a loaded title: platform field defaults and the global
///     source priority choose each effective field, then primary media is recalculated.
///     The caller owns persistence.
/// </summary>
public sealed class TitleMetadataRematerializer(
    IPlatformFieldDefaultRepository platformFieldDefaults,
    IOptions<EnrichmentOptions> options) : IMetadataRematerializer
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task RematerializeAsync(Title title, CancellationToken ct = default)
    {
        var platformDefaults = await platformFieldDefaults.GetByPlatformIdAsync(title.PlatformId, ct);
        title.Rematerialize(_options.GlobalSourcePriority, platformDefaults);
        title.RecalculatePrimaryMedia(_options.GlobalSourcePriority);
    }
}
