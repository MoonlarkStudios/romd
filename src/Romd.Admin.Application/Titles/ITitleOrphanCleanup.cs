namespace Romd.Admin.Application.Titles;

/// <summary>
///     Port for the source-backing loss policy: a title that lost all its source entries is deleted,
///     or retained as UserOnly when it carries user or curated state.
/// </summary>
public interface ITitleOrphanCleanup
{
    /// <summary>
    ///     Gets ids of titles with no source backing (zero truth-level links) that are not
    ///     already retained as UserOnly and were created before the cutoff — the grace window
    ///     protects titles mid-derivation between creation and linking. Backstop input for
    ///     the convergence sweep's orphan policy.
    /// </summary>
    Task<IReadOnlyList<int>> GetOrphanedTitleIdsAsync(
        DateTimeOffset createdBefore,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Whether any source entry is still assigned to the title.</summary>
    Task<bool> HasSourceEntriesAsync(int titleId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes a title that has lost all its source backing. When the title still carries
    ///     user/curated state (tracking, field overrides, confirmed external IDs, primary media,
    ///     collection membership) it is retained and marked <c>UserOnly</c> instead of deleted.
    ///     Zero links are re-checked atomically at mutation time, so a title re-linked by a
    ///     concurrent derivation after the caller's orphan check loses nothing (the call then
    ///     mutates nothing and returns <c>false</c>). Hard deletion re-points releases that a
    ///     surviving source still asserts (release identity is public and fingerprint-stable)
    ///     and deletes only the releases nobody else asserts. PRECONDITION (enforced): a
    ///     caller-owned transaction must be open — the mutations span multiple statements.
    ///     Returns <c>true</c> when the title was hard-deleted, <c>false</c> otherwise.
    /// </summary>
    Task<bool> DeleteOrRetainAsync(int titleId, CancellationToken cancellationToken = default);
}
