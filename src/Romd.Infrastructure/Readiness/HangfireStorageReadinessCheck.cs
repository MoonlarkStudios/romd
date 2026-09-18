using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Critical (all API hosts): Hangfire PostgreSQL must be reachable and the worker must have
///     published the exact schema version this release expects. A reachable database with a
///     missing or stale schema fails closed.
/// </summary>
public sealed class HangfireStorageReadinessCheck(IHangfireSchemaProbe probe) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.HangfireStorage;

    public bool IsCritical => true;

    public async Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        int? version = await probe.GetPublishedVersionAsync(cancellationToken);
        return version == HangfirePostgreSqlConfiguration.ExpectedSchemaVersion
            ? ReadinessCheckStatus.Healthy
            : ReadinessCheckStatus.Unready;
    }
}
