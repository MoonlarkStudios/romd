using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Titles.Enrichment;

public interface IEnrichmentJobAcceptance
{
    /// <summary>Locks this title's acceptance key through commit and checks Pending or Enriching work.</summary>
    Task<bool> LockAndHasPendingForTitleStagedAsync(int titleId, CancellationToken ct = default);

    /// <summary>Stages the job in the required caller-owned transaction without flushing or committing.</summary>
    Task AddStagedAsync(EnrichmentJob job, CancellationToken ct = default);
}

public interface IBulkEnrichmentJobAcceptance
{
    /// <summary>Locks this platform's acceptance key and returns existing Pending or Enriching work, irrespective of scope or actor.</summary>
    Task<Guid?> LockAndGetPendingForPlatformStagedAsync(int platformId, CancellationToken ct = default);

    /// <summary>Stages the job in the required caller-owned transaction without flushing or committing.</summary>
    Task AddStagedAsync(BulkEnrichmentJob job, CancellationToken ct = default);
}
