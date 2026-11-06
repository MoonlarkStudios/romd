using System.Collections.Immutable;

namespace Romd.Persistence.ReferenceData;

internal static class CatalogArtworkImport
{
    internal sealed record Icon(string Path, ImmutableArray<byte> Content);

    /// <summary>
    ///     Reads reviewed embedded icons only after bootstrap validation and alias pruning. This
    ///     bounded preparation intentionally stays inside RomdCatalogImporter's catalog exclusion;
    ///     interactive application commands prepare content before their short transaction instead.
    /// </summary>
    internal static async Task<ImmutableArray<Icon>> ReadPlanAsync(RomdCatalogInput catalog, CancellationToken ct)
    {
        var icons = ImmutableArray.CreateBuilder<Icon>();
        foreach (var path in catalog.Systems.Values.Select(x => x.IconPath).Concat(catalog.Ratings.Ratings.Select(x => x.IconPath)).OfType<string>().Distinct())
        {
            using var stream = typeof(CatalogArtworkImport).Assembly.GetManifestResourceStream("ReferenceAssets/" + path)
                ?? throw new InvalidDataException("Missing built-in reference icon: " + path);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            icons.Add(new Icon(path, ImmutableArray.CreateRange(buffer.ToArray())));
        }
        return icons.ToImmutable();
    }

    internal static async Task<IReadOnlyDictionary<string, string>> ImportAsync(RomdDbContext db,
        ImmutableArray<Icon> icons, IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        if (icons.Length != fileIds.Count)
            throw new ArgumentException("Reference publication must preserve every icon position.", nameof(fileIds));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < icons.Length; i++)
            result.Add(icons[i].Path, await ReferenceCatalogService.RecordAssetAsync(db, icons[i].Content.ToArray(), fileIds[i],
                icons[i].Path.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : "image/png", ct));
        await db.SaveChangesAsync(ct);
        return result;
    }
}
