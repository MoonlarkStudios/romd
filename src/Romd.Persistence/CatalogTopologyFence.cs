using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence;

/// <summary>
///     Transaction-scoped PostgreSQL advisory lock shared by every catalog-topology writer
///     (DAT delete/replace, title moves, derivation, and source-link writers). Row locks cannot
///     serialize writers that touch different tables; one advisory key can. Consumers that write
///     against the current topology take it in shared mode. The caller-owned transaction releases it.
/// </summary>
public static class CatalogTopologyFence
{
    private const string LockName = "romd.catalog-topology";

    public static Task AcquireAsync(DbContext context, CancellationToken cancellationToken)
    {
        RequireTransaction(context);
        return context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({LockName}))",
            cancellationToken);
    }

    public static Task AcquireSharedAsync(DbContext context, CancellationToken cancellationToken)
    {
        RequireTransaction(context);
        return context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock_shared(hashtext({LockName}))",
            cancellationToken);
    }

    private static void RequireTransaction(DbContext context)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The catalog topology fence requires a caller-owned transaction.");
        }
    }
}
