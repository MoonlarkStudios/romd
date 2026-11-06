using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Pagination;
using Romd.Domain.ReferenceData;
using Romd.Persistence.ReferenceData;

namespace Romd.Persistence.Repositories;

/// <summary>The parts of region and language persistence both repositories share.</summary>
internal static class TaxonomyEntryProjection
{
    /// <summary>
    ///     The advisory lock that serializes minting unregistered region and language keys. One key covers both
    ///     collections: an ingest holds it until commit and mints both kinds in either order, so a key per
    ///     collection would let two ingests deadlock.
    /// </summary>
    private const int MintingLockFamily = 150;
    private const int MintingLockKey = 1;
    private const string MintSavepoint = "taxonomy_mint";

    /// <summary>
    ///     Takes the minting lock until the caller's transaction ends; reentrant. Minting outside a transaction is a
    ///     programming error, since nothing would hold the lock until the new key commits.
    /// </summary>
    internal static Task LockMintingAsync(RomdDbContext context, CancellationToken ct) =>
        context.Database.CurrentTransaction is null
            ? throw new InvalidOperationException(
                "Minting an unregistered region or language key requires the caller's transaction.")
            : JobAcceptanceLock.AcquireAsync(context, MintingLockFamily, MintingLockKey, ct);

    /// <summary>
    ///     Runs a database-only <paramref name="operation" /> under a savepoint. Pending changes are saved first,
    ///     so the savepoint covers only the mint. A refused alias rolls the savepoint back and
    ///     forgets every entity the attempt tracked, audit evidence included, so nothing of it is saved later.
    /// </summary>
    internal static async Task<int?> MintAsync(RomdDbContext context, Func<CancellationToken, Task<int?>> operation,
        CancellationToken ct)
    {
        var transaction = context.Database.CurrentTransaction ?? throw new InvalidOperationException(
            "Minting an unregistered region or language key requires the caller's transaction.");
        await context.SaveChangesAsync(ct);
        var tracked = context.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        await transaction.CreateSavepointAsync(MintSavepoint, ct);
        if (await operation(ct) is { } id)
        {
            await transaction.ReleaseSavepointAsync(MintSavepoint, ct);
            return id;
        }

        await transaction.RollbackToSavepointAsync(MintSavepoint, ct);
        await transaction.ReleaseSavepointAsync(MintSavepoint, ct);
        foreach (var entry in context.ChangeTracker.Entries().Where(entry => !tracked.Contains(entry.Entity)).ToList())
            entry.State = EntityState.Detached;
        return null;
    }

    /// <summary>Sets <c>lock_timeout</c> until the caller's transaction ends; without one there is nothing to bound.</summary>
    internal static Task LimitLockWaitsAsync(RomdDbContext context, TimeSpan timeout, CancellationToken ct) =>
        context.Database.CurrentTransaction is null
            ? throw new InvalidOperationException("Bounding lock waits requires the caller's transaction.")
            : context.Database.ExecuteSqlAsync(
                $"SELECT set_config('lock_timeout', {$"{(long)timeout.TotalMilliseconds}ms"}, true)", ct);

    /// <summary>
    ///     Locks each existing row in its mode, one statement per row in id order, so lockers of several rows cannot
    ///     deadlock. <paramref name="statement" /> is the repository's parameterized lock query for one row and mode.
    /// </summary>
    internal static async Task<IReadOnlySet<int>> LockRowsAsync(RomdDbContext context,
        IReadOnlyCollection<(int Id, TaxonomyRowLock Mode)> rows, Func<int, TaxonomyRowLock, FormattableString> statement,
        CancellationToken ct)
    {
        var locked = new HashSet<int>();
        foreach (var (id, mode) in rows.OrderBy(row => row.Id))
            // Executed as written: composing over the locking SELECT would wrap it in a subquery.
            if ((await context.Database.SqlQuery<int>(statement(id, mode)).ToListAsync(ct)).Count > 0)
                locked.Add(id);
        return locked;
    }

    /// <summary>
    ///     The first alias page a row embeds, from its first <see cref="TaxonomyAliasPageRequest.EmbeddedLimit" /> aliases
    ///     and one more, if there is one, which shows that its alias collection continues.
    /// </summary>
    internal static TaxonomyAliasPage FirstPage(IEnumerable<TaxonomyAlias> aliases)
    {
        var first = aliases.OrderBy(alias => alias.Id).ToArray();
        return new TaxonomyAliasPage(first.Take(TaxonomyAliasPageRequest.EmbeddedLimit).ToArray(),
            first.Length > TaxonomyAliasPageRequest.EmbeddedLimit);
    }

    /// <summary>A page of the request's limit from up to one alias more, read in id order after its cursor.</summary>
    internal static PagedList<TaxonomyAlias> AliasPage(IReadOnlyList<TaxonomyAlias> aliases, TaxonomyAliasPageRequest request)
    {
        bool hasNext = aliases.Count > request.Limit;
        var items = aliases.Take(request.Limit).ToArray();
        return new PagedList<TaxonomyAlias>(items, hasNext ? request.CursorFor(items[^1].Id) : null, hasNext);
    }

    /// <summary>Stamps the entry with a revision over its whole projection, its embedded alias page included.</summary>
    internal static TaxonomyEntry Versioned(TaxonomyEntry entry) => entry with { Revision = TypedReferenceMapping.Revision(entry) };

    /// <summary>An alias without recorded provenance reads as <c>Unresolved</c>.</summary>
    internal static string AliasOwnership(ReferenceOwnership? ownership) => ownership?.ToString() ?? "Unresolved";
}
