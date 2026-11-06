using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence;

/// <summary>
///     Transaction-scoped PostgreSQL advisory lock shared by every catalog-topology writer
///     (DAT delete/replace, title moves, derivation, and source-link writers). Row locks cannot
///     serialize writers that touch different tables; one advisory key can. The lock is released
///     automatically when the caller-owned transaction commits or rolls back.
/// </summary>
public static class CatalogTopologyFence
{
    private const string LockName = "romd.catalog-topology";

    public static Task AcquireAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The catalog topology fence requires a caller-owned transaction.");
        }

        return context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({LockName}))",
            cancellationToken);
    }
}
