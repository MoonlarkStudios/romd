using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static class RatingCatalogImport
{
    internal static async Task ValidateAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var row in await db.Ratings.ToListAsync(ct))
            RomdCatalogImporter.ValidateIdentity("Rating", row.Key!, row.Ownership, catalog.Ratings.Ratings.Any(x => x.Board + ":" + x.Code == row.Key));
    }
    internal static async Task ImportAsync(RomdDbContext db, RomdCatalogInput catalog, IReadOnlyDictionary<string, string> assets, CancellationToken ct)
    {
        foreach (var facts in catalog.Ratings.Ratings)
        {
            var key = facts.Board + ":" + facts.Code;
            var row = await db.Ratings.AsTracking().SingleOrDefaultAsync(x => x.Key == key, ct);
            if (row is null) { row = new RatingEntity { Key = key, BoardKey = facts.Board, Code = facts.Code }; db.Ratings.Add(row); }
            row.Ownership = ReferenceOwnership.Romd; row.BuiltInVersion = catalog.CatalogVersion;
            row.BaseName = facts.Name; row.BaseDescription = facts.Description; row.Retired = facts.Retired;
            row.Designation = facts.Designation; row.MinimumAge = facts.MinimumAge;
            row.BaseAssetHash = facts.IconPath is null ? null : assets[facts.IconPath]; row.BaseMonochrome = facts.Monochrome;
        }
        await db.SaveChangesAsync(ct);
    }
}
