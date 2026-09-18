using Romd.Domain.Catalog;
using Romd.Domain.Hashing;

namespace Romd.Application.Common.Artwork;

/// <summary>Reads retained artwork only. Callers authorize title access before requesting resolutions.</summary>
public interface IArtworkReader
{
    Task<IReadOnlyDictionary<int, IReadOnlyList<ArtworkResolution>>> ResolveAsync(
        IReadOnlyCollection<int> titleIds, CancellationToken ct = default);

    Task<ArtworkFileReference?> FindVariantAsync(int assetId, string name, string contentVersion,
        CancellationToken ct = default);
}

public sealed record ArtworkFileReference(Sha256 Sha256, string ContentType);
