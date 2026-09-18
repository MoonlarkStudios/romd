using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static class RegionCatalogImport
{
    internal static async Task ValidateAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var row in await db.Regions.Where(x => x.CanonicalKey != null).ToListAsync(ct))
            RomdCatalogImporter.ValidateIdentity("Region", row.CanonicalKey!, row.Ownership, catalog.Regions.Keys.Contains(row.CanonicalKey!));
    }
    internal static async Task ImportAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var (key, facts) in catalog.Regions)
        {
            var row = await db.Regions.AsTracking().SingleOrDefaultAsync(x => x.CanonicalKey == key, ct);
            if (row is null) { row = new RegionEntity { CanonicalKey = key, Name = facts.Name }; db.Regions.Add(row); }
            row.Ownership = ReferenceOwnership.Romd;
            row.BuiltInVersion = catalog.CatalogVersion;
            row.BaseName = facts.Name;
            row.BaseDescription = facts.Description;
            row.BaseSortOrder = facts.SortOrder;
            row.Retired = facts.Retired;
            await db.SaveChangesAsync(ct);
            await CatalogAliasReconciliation.RegionAsync(db, row.Id, facts.Aliases.Concat([facts.Name]), ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
