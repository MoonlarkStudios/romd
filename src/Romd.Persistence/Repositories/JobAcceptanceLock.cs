using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence.Repositories;

/// <summary>Serializes a dedup-key lookup through the caller's acceptance commit.</summary>
internal static class JobAcceptanceLock
{
    internal static Task AcquireAsync(RomdDbContext context, int family, int key, CancellationToken ct)
    {
        // Read-only callers need no lock. Acceptance callers own a transaction around lookup
        // and insertion, including when no matching job exists yet (a row lock cannot do that).
        if (context.Database.CurrentTransaction is null) return Task.CompletedTask;
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({family}, {key})", ct);
    }
}
