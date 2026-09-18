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
    /// <remarks>This operation is an immediate persistence flush that participates in the caller's ambient transaction.</remarks>
    Task UpsertAssignmentsAsync(
        IReadOnlyList<TitleSourceAssignment> assignments,
        CancellationToken cancellationToken = default);

    Task ClearAssignmentsAsync(
        IReadOnlyList<int> sourceEntryIds,
        CancellationToken cancellationToken = default);
}

public sealed record TitleSourceAssignment(int SourceEntryId, int TitleId);

public sealed record TitleSourceAssignmentContext(
    int SourceEntryId,
    int? PlatformId,
    int? TitleId);
