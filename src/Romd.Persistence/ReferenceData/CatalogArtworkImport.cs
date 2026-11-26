using System.Collections.Immutable;

namespace Romd.Persistence.ReferenceData;

internal static class CatalogArtworkImport
{
    internal sealed record Artwork(string Path, ImmutableArray<byte> Content);

    /// <summary>
    ///     Reads reviewed embedded artwork only after bootstrap validation and alias pruning. This
    ///     bounded preparation intentionally stays inside RomdCatalogImporter's catalog exclusion;
    ///     interactive application commands prepare content before their short transaction instead.
    /// </summary>
    internal static async Task<ImmutableArray<Artwork>> ReadPlanAsync(RomdCatalogInput catalog, CancellationToken ct)
    {
        var artwork = ImmutableArray.CreateBuilder<Artwork>();
        var paths = catalog.Systems.Values.SelectMany(system => new[] { system.IconPath, system.ConsoleArtworkPath, system.ControllerArtworkPath })
            .Concat(catalog.Ratings.Ratings.Select(rating => rating.IconPath)).OfType<string>().Distinct();
        foreach (var path in paths)
        {
            using var stream = typeof(CatalogArtworkImport).Assembly.GetManifestResourceStream("ReferenceAssets/" + path)
                ?? throw new InvalidDataException("Missing built-in reference artwork: " + path);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            artwork.Add(new Artwork(path, ImmutableArray.CreateRange(buffer.ToArray())));
        }
        return artwork.ToImmutable();
    }

    internal static async Task<IReadOnlyDictionary<string, string>> ImportAsync(RomdDbContext db,
        ImmutableArray<Artwork> artwork, IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        if (artwork.Length != fileIds.Count)
            throw new ArgumentException("Reference publication must preserve every artwork position.", nameof(fileIds));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < artwork.Length; i++)
        {
            var asset = artwork[i];
            string contentType = Path.GetExtension(asset.Path) switch
            {
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => throw new InvalidDataException("Unsupported reference artwork format: " + asset.Path)
            };
            result.Add(asset.Path, await ReferenceCatalogService.RecordAssetAsync(db, asset.Content.ToArray(), fileIds[i], contentType, ct));
        }
        await db.SaveChangesAsync(ct);
        return result;
    }
}
