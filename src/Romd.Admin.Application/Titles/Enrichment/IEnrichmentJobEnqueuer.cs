namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Accelerates dispatch of an already committed enrichment job.
/// </summary>
public interface IEnrichmentJobEnqueuer
{
    Task EnqueueAsync(Guid jobId, CancellationToken ct = default);
}
