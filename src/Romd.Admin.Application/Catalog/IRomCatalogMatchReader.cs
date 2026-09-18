using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Reads the catalog meaning currently assigned to ROM content.
/// </summary>
public interface IRomCatalogMatchReader
{
    Task<RomCatalogMatch> ReadAsync(
        Sha1 sha1,
        CancellationToken cancellationToken = default);
}

public sealed record RomCatalogMatch(
    IReadOnlyList<int> TitleIds,
    IReadOnlyList<int> TitlePlatformIds,
    IReadOnlyList<int> BiosPlatformIds)
{
    public bool HasCatalogMatch => TitleIds.Count > 0 || BiosPlatformIds.Count > 0;

    /// <summary>
    ///     Gets a title platform when available, otherwise a BIOS platform. Selection among
    ///     multiple matched platforms is unspecified.
    /// </summary>
    public int? PrimaryPlatformId =>
        TitlePlatformIds.Count > 0
            ? TitlePlatformIds[0]
            : BiosPlatformIds.Count > 0
                ? BiosPlatformIds[0]
                : null;
}
