using Romd.Application.Common.ReferenceCatalog;

namespace Romd.Persistence.ReferenceData;

internal static class CatalogArtworkImport
{
    internal static async Task<IReadOnlyDictionary<string, string>> ImportAsync(RomdDbContext db, IReferenceBlobStore blobs, RomdCatalogInput catalog, CancellationToken ct)
    {
        var icons = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in catalog.Systems.Values.Select(x => x.IconPath).Concat(catalog.Ratings.Ratings.Select(x => x.IconPath)).OfType<string>().Distinct())
        {
            using var stream = typeof(CatalogArtworkImport).Assembly.GetManifestResourceStream("ReferenceAssets/" + path)
                ?? throw new InvalidDataException("Missing built-in reference icon: " + path);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            icons.Add(path, buffer.ToArray());
        }
        foreach (var hash in icons.Values.Select(ReferenceCatalogService.Hash).Distinct().Order(StringComparer.Ordinal))
            await new Romd.Persistence.Repositories.FileMutationLock(db).AcquireAsync(Romd.Domain.Hashing.Sha256.Parse(hash), ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, bytes) in icons)
            result.Add(path, await ReferenceCatalogService.StoreAssetAsync(db, blobs, bytes, path.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : "image/png", ct));
        await db.SaveChangesAsync(ct);
        return result;
    }
}
