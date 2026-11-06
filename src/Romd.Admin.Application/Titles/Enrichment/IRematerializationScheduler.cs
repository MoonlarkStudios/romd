namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>Stages internal per-title convergence in the caller-owned transaction.</summary>
public interface IRematerializationScheduler
{
    Task EnqueueTitleAsync(int titleId, CancellationToken ct = default);
}
