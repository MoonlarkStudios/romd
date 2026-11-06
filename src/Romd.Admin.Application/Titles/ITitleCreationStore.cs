using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

/// <summary>
///     Port for title identity by unique (platform, normalized name): look up existing titles and create
///     new ones for ingest and game moves.
/// </summary>
public interface ITitleCreationStore
{
    /// <summary>Whether the platform already has a title with this normalized name.</summary>
    Task<bool> ExistsWithNormalizedNameAsync(
        int platformId,
        string normalizedName,
        CancellationToken cancellationToken = default);

    /// <summary>Maps each existing normalized name on the platform to its title ID.</summary>
    Task<IReadOnlyDictionary<string, int>> FindIdsByNormalizedNamesAsync(
        int platformId,
        IReadOnlyCollection<string> normalizedNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages a new, identity-free title inside the caller's active transaction. The caller owns
    ///     flushing creation and reads the generated identity from this insertion's receipt.
    /// </summary>
    Task<IStagedTitleCreation> AddStagedAsync(Title title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages new titles for the bounded derivation protocol. Its existing batch owner flushes
    ///     identities before reading them, enlisting in the caller transaction when present or using
    ///     standalone per-flush durability. This method never flushes or begins a transaction.
    /// </summary>
    Task<IStagedTitleCreationBatch> AddBatchStagedAsync(IReadOnlyList<Title> titles, CancellationToken cancellationToken = default);
}

/// <summary>
///     Reads this insertion's generated identity after the caller flushes. The receipt belongs to
///     the current unit-of-work scope; reading it does not flush or commit creation.
/// </summary>
public interface IStagedTitleCreation
{
    int ReadIdAfterFlush();
}

/// <summary>
///     Own insertion identities in input order. Transaction-bound insertions cannot outlive that
///     transaction; standalone insertions become durable at their owner's identity flush. Clearing
///     or rolling back their tracked staging invalidates the receipt, even if IDs were allocated.
/// </summary>
public interface IStagedTitleCreationBatch
{
    bool RequiresFlush { get; }
    IReadOnlyList<int> ReadIdsAfterFlush();
}
