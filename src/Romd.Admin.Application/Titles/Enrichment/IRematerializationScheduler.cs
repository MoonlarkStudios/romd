namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Stages durable metadata rematerialization intent in the caller-owned transaction.
///     The caller must commit; execution and retries belong to the worker.
/// </summary>
public interface IRematerializationScheduler
{
    Task EnqueueTitleAsync(int titleId, CancellationToken ct = default);

    Task EnqueuePlatformAsync(int platformId, CancellationToken ct = default);

    Task EnqueueAllAsync(CancellationToken ct = default);
}
