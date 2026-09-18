using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static class LanguageCatalogImport
{
    internal static async Task ValidateAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var row in await db.GameLanguages.Where(x => x.CanonicalKey != null).ToListAsync(ct))
            RomdCatalogImporter.ValidateIdentity("Language", row.CanonicalKey!, row.Ownership, catalog.Languages.Keys.Contains(row.CanonicalKey!));
    }
    internal static async Task ImportAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var (key, facts) in catalog.Languages)
        {
            var row = await db.GameLanguages.AsTracking().SingleOrDefaultAsync(x => x.CanonicalKey == key, ct);
            if (row is null) { row = new GameLanguageEntity { CanonicalKey = key, Name = facts.Name, Code = key }; db.GameLanguages.Add(row); }
            row.Ownership = ReferenceOwnership.Romd;
            row.BuiltInVersion = catalog.CatalogVersion;
            row.BaseName = facts.Name;
            row.BaseDescription = facts.Description;
            row.BaseSortOrder = facts.SortOrder;
            row.Retired = facts.Retired;
            await db.SaveChangesAsync(ct);
            await CatalogAliasReconciliation.LanguageAsync(db, row.Id, facts.Aliases.Concat([facts.Name, key]), ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
