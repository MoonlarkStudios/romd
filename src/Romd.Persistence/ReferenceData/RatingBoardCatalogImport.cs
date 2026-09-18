using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static class RatingBoardCatalogImport
{
    internal static async Task ValidateAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var row in await db.RatingBoards.ToListAsync(ct))
            RomdCatalogImporter.ValidateIdentity("RatingBoard", row.Key!, row.Ownership, catalog.Ratings.Boards.Any(x => x.Key == row.Key));
    }
    internal static async Task ImportAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        foreach (var facts in catalog.Ratings.Boards)
        {
            var row = await db.RatingBoards.AsTracking().SingleOrDefaultAsync(x => x.Key == facts.Key, ct);
            if (row is null) { row = new RatingBoardEntity { Key = facts.Key }; db.RatingBoards.Add(row); }
            row.Ownership = ReferenceOwnership.Romd; row.BuiltInVersion = catalog.CatalogVersion;
            row.BaseName = facts.Label; row.BaseDescription = facts.Description; row.Retired = facts.Retired;
        }
        await db.SaveChangesAsync(ct);
    }
}
