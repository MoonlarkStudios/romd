using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Port for BIOS catalog data access. BIOS entries are the firmware analog of
///     <see cref="Title" />: they group BIOS DatGames per platform and expose ownership.
/// </summary>
public interface IBiosRepository
{
    /// <summary>
    ///     Gets existing BIOS entries by normalized name for batch matching.
    ///     Returns a dictionary mapping normalized name to BIOS entry for efficient lookup.
    /// </summary>
    Task<Dictionary<string, Bios>> GetByNormalizedNamesAsync(
        int platformId,
        IEnumerable<string> normalizedNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds multiple BIOS entries in a batch and returns them with database-assigned IDs.
    /// </summary>
    Task<IReadOnlyList<Bios>> AddRangeAsync(
        IReadOnlyList<Bios> bios,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Maps BIOS DatGames to their canonical BIOS entries in a batch.
    /// </summary>
    Task AddGameMappingsAsync(
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
///     A BIOS DatGame awaiting grouping into a platform-scoped BIOS entry.
/// </summary>
public sealed record UnmappedBiosGame(int GameId, int PlatformId, string Name);
