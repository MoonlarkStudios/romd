using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static class SystemCatalogImport
{
    internal static async Task ValidateAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var row in await db.Platforms.Where(x => x.CanonicalKey != null).ToListAsync(ct))
            RomdCatalogImporter.ValidateIdentity("System", row.CanonicalKey!, row.Ownership, catalog.Systems.Keys.Contains(row.CanonicalKey!));
    }
    internal static async Task ImportAsync(RomdDbContext db, RomdCatalogInput catalog, IReadOnlyDictionary<string, string> assets, CancellationToken ct)
    {
        var repository = new SystemReferenceRepository(db);
        foreach (var (key, facts) in catalog.Systems)
        {
            var row = await db.Platforms.SingleOrDefaultAsync(x => x.CanonicalKey == key, ct);
            await repository.StageAsync(new(row?.Id ?? 0, key, new(ReferenceOwnership.Romd, catalog.CatalogVersion),
                new(facts.Name, facts.CompactLabel, facts.Description, facts.IconPath is null ? null : assets[facts.IconPath], facts.Monochrome, facts.Retired),
                row?.ReferenceOverrides ?? new(), facts.ManufacturerIds), ct);
            await db.SaveChangesAsync(ct);
            var id = await db.Platforms.Where(x => x.CanonicalKey == key).Select(x => x.Id).SingleAsync(ct);
            await CatalogAliasReconciliation.SystemAsync(db, id, facts, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
