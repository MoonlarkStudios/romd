using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Readiness;
using Romd.Persistence;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Critical: the database must be reachable and the schema current. Pending EF migrations report
///     unready — the API hosts never apply migrations (the worker owns them), so "schema behind" is a
///     truthful not-ready signal, not a repair trigger.
/// </summary>
public sealed class DatabaseReadinessCheck(RomdDbContext context) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.Database;

    public bool IsCritical => true;

    public async Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return ReadinessCheckStatus.Unready;
        }

        var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        return pendingMigrations.Any() ? ReadinessCheckStatus.Unready : ReadinessCheckStatus.Healthy;
    }
}
