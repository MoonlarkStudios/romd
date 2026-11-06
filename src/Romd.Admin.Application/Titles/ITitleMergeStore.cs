using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

/// <summary>Port for the writes a title merge stages inside the caller's transaction.</summary>
public interface ITitleMergeStore
{
    /// <summary>
    ///     Stages a merge target after <see cref="Title.Absorb" />: verifies and locks both snapshot
    ///     revisions in ID order, then writes the target additively, as an enrichment result is written.
    ///     Deleting the source remains a separate step. The caller owns the flush and commit.
    /// </summary>
    Task ApplyMergeStagedAsync(Title target, Title source, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The providers the curator unlinked on the title, whose link and metadata a merge must not absorb. Read
    ///     before the merge's transaction: a later unlink rotates the title, so the staged merge rejects its snapshot.
    /// </summary>
    Task<IReadOnlyList<string>> GetUnlinkedProvidersAsync(int titleId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Moves retained artwork and consolidates role selections inside a caller-owned transaction.
    ///     The caller owns the flush and commit; source pending imports are invalidated.
    /// </summary>
    Task StageArtworkMergeAsync(int sourceTitleId, int targetTitleId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the absorbed merge source, unless tracked intent still requires retaining it as
    ///     UserOnly. Merge consolidates tracking first, so the source is normally hard-deleted.
    /// </summary>
    Task DeleteAbsorbedSourceAsync(int sourceTitleId, CancellationToken cancellationToken = default);
}
