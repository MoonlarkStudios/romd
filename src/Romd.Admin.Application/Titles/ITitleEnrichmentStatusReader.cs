using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

/// <summary>Read port for title enrichment status: bulk selection and progress counts.</summary>
public interface ITitleEnrichmentStatusReader
{
    /// <summary>
    ///     Gets a platform's titles whose status is None, Pending, or LowConfidence, ordered by name.
    ///     <paramref name="scope" /> restricts selection to tracked titles or includes every title.
    /// </summary>
    Task<IReadOnlyList<EnrichmentCandidate>> GetCandidatesAsync(
        int platformId,
        EnrichmentScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>Counts enrichment status over the tracked title set, the default bulk scope.</summary>
    Task<TitleEnrichmentStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
