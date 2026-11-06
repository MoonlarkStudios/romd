namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Persists the catalog's assignment of source entries to canonical titles.
/// </summary>
public interface ITitleSourceAssignmentStore
{
    Task<TitleSourceAssignmentContext?> GetAssignmentContextAsync(
        int sourceEntryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> GetSourceEntryIdsByTitleAsync(
        int titleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Curation-path write: inserts or overwrites assignments. Derivation-path writes go
    ///     through <see cref="ITitleDerivationService" />, which never overwrites curation.
    /// </summary>
    /// <remarks>
    ///     Requires the caller's active transaction. Changes remain staged until the caller flushes
    ///     or commits; flush before database reads that depend on the new assignments.
    /// </remarks>
    Task UpsertAssignmentsAsync(
        IReadOnlyList<TitleSourceAssignment> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears the selected assignments, including earlier staged upserts for those entries.
    ///     A subsequent upsert may assign them again; unrelated staged changes are preserved.
    /// </summary>
    Task ClearAssignmentsAsync(
        IReadOnlyList<int> sourceEntryIds,
        CancellationToken cancellationToken = default);
}

public sealed record TitleSourceAssignment(int SourceEntryId, int TitleId);

public sealed record TitleSourceAssignmentContext(
    int SourceEntryId,
    int? PlatformId,
    int? TitleId);
