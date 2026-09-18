namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Groups BIOS DatGames into platform-scoped BIOS entries (match-or-create by normalized name)
///     and maps each game to its entry. The firmware analog of title matching for non-BIOS games.
/// </summary>
public interface IBiosGrouper
{
    /// <summary>
    ///     Groups BIOS games for a single platform. Returns the number of new BIOS entries created.
    /// </summary>
    Task<int> GroupAsync(
        int platformId,
        IReadOnlyList<(int GameId, string Name)> biosGames,
        CancellationToken cancellationToken = default);
}
