using Romd.Application.Common.Readiness;
using Romd.Persistence;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Fails closed until the worker has migrated the <c>romd</c> schema, verified its search
///     documents, and published the expected version.
/// </summary>
public sealed class ApplicationSchemaReadinessCheck(IRomdSchemaProbe probe) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.ApplicationSchema;

    public bool IsCritical => true;

    public async Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        int? version = await probe.GetPublishedVersionAsync(cancellationToken);
        return version == PostgreSqlConfiguration.ExpectedSchemaVersion
            ? ReadinessCheckStatus.Healthy
            : ReadinessCheckStatus.Unready;
    }
}
