using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Acquires one provider media asset and stages it for publication with its title. Implementations own
///     transport, content detection and temporary storage; selection and curation policy stay with the caller.
/// </summary>
public interface IEnrichmentMediaStager
{
    /// <summary>
    ///     Returns staged media the caller owns and must dispose, or null when the asset could not be acquired.
    ///     Cancellation propagates after releasing anything partially staged.
    /// </summary>
    Task<StagedTitleMedia?> StageAsync(
        int titleId,
        string providerId,
        MediaType mediaType,
        string url,
        CancellationToken cancellationToken);
}
