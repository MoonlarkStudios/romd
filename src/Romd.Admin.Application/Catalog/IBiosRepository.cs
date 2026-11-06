using Romd.Domain.Catalog;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Port for BIOS catalog data access. BIOS entries are the firmware analog of
///     <see cref="Title" />: they group BIOS DatGames per platform and expose ownership.
/// </summary>
public interface IBiosRepository
{
    Task<PagedList<BiosOwnership>> ListPageAsync(int platformId, int? after, int limit, CancellationToken ct = default);

    /// <summary>
    ///     Gets existing BIOS entries by normalized name for batch matching.
    ///     Returns a dictionary mapping normalized name to BIOS entry for efficient lookup.
    /// </summary>
    Task<Dictionary<string, Bios>> GetByNormalizedNamesAsync(
        int platformId,
        IEnumerable<string> normalizedNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages new, identity-free BIOS entries in the caller's transaction. The caller flushes
    ///     and reads this batch's generated identities from its receipt, in input order.
    /// </summary>
    Task<IStagedBiosCreationBatch> AddRangeStagedAsync(
        IReadOnlyList<Bios> bios,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages mappings from BIOS DatGames to their canonical entries in the caller's
    ///     transaction. The caller owns flushing and committing them.
    /// </summary>
    Task AddGameMappingsStagedAsync(
        IReadOnlyList<(int DatGameId, int BiosId)> mappings,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all BIOS entries for a platform with derived ownership counts, ordered by name.
    /// </summary>
    Task<IReadOnlyList<BiosOwnership>> GetByPlatformWithOwnershipAsync(
        int platformId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets BIOS DatGames that belong to a platform-assigned DAT but are not yet grouped into a
    ///     BIOS entry. Used to backfill the catalog for DATs assigned before BIOS grouping existed.
    /// </summary>
    Task<IReadOnlyList<UnmappedBiosGame>> GetUnmappedBiosGamesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Reads this batch's BIOS entries after the caller flushes staging. This scope-bound receipt
///     captures its own insertions; reading it does not flush or commit them.
/// </summary>
public interface IStagedBiosCreationBatch
{
    IReadOnlyList<Bios> ReadAfterFlush();
}

/// <summary>
///     A BIOS DatGame awaiting grouping into a platform-scoped BIOS entry.
/// </summary>
public sealed record UnmappedBiosGame(int GameId, int PlatformId, string Name);
