using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static class CompanyCatalogImport
{
    internal static async Task ValidateAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var row in await db.Companies.ToListAsync(ct))
            RomdCatalogImporter.ValidateIdentity("Company", row.Key!, row.Ownership, catalog.Companies.Keys.Contains(row.Key));
    }
    internal static async Task ImportAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        var repository = new CompanyReferenceRepository(db);
        foreach (var (key, facts) in catalog.Companies)
        {
            var previous = await repository.GetAsync(key, ct);
            await repository.StageAsync(new(key, new(ReferenceOwnership.Romd, catalog.CatalogVersion), new(facts.Name, facts.Description, facts.Retired), previous?.Overrides ?? new()), ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
