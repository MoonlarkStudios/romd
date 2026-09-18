using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Builds EnrichmentContext for a title by selecting a representative game
///     and collecting owned ROM hashes.
/// </summary>
public interface IEnrichmentContextFactory
{
    /// <summary>
    ///     Builds an EnrichmentContext for the given title.
    /// </summary>
    Task<EnrichmentContext> CreateAsync(
        Title title,
        Platform platform,
        CancellationToken cancellationToken = default);
}
