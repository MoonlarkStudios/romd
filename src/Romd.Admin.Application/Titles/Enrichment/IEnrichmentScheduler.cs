using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Schedules enrichment tasks for titles.
/// </summary>
public interface IEnrichmentScheduler
{
    /// <summary>
    ///     Enqueues an enrichment task for a title.
    ///     Deduplicates against pending tasks.
    /// </summary>
    Task EnqueueAsync(int titleId, string titleName, int platformId, CancellationToken ct = default);

    /// <summary>
    ///     Enqueues enrichment tasks for multiple titles.
    /// </summary>
    Task EnqueueBatchAsync(
        IEnumerable<(int TitleId, string TitleName, int PlatformId)> titles,
        CancellationToken ct = default);

    /// <summary>
    ///     Enqueues a bulk enrichment job for a platform.
    ///     Deduplicates against pending/active bulk enrichment jobs.
    /// </summary>
    /// <param name="platformId">The platform to enrich.</param>
    /// <param name="scope">Whether to enrich only tracked titles (default) or every title.</param>
    /// <param name="createdByUserId">The user that initiated the run, if any.</param>
    /// <param name="ct">The cancellation token.</param>
    Task EnqueueBulkAsync(
        int platformId,
        EnrichmentScope scope = EnrichmentScope.Tracked,
        Guid? createdByUserId = null,
        CancellationToken ct = default);
}
