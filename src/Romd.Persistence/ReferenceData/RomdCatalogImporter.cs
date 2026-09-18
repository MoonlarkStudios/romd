using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Persistence.ReferenceData;

/// <summary>Coordinates typed imports and publishes one atomic effective catalog.</summary>
internal sealed class RomdCatalogImporter
{
    internal static async Task ImportAsync(RomdDbContext db, IReferenceBlobStore blobs, RomdCatalogInput catalog, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ReferenceCatalogService.LockAsync(db, ct);
            var installed = await db.ReferenceCatalogState.Select(x => (int?)x.BuiltInVersion).SingleOrDefaultAsync(ct);
            if (installed > catalog.CatalogVersion) throw new InvalidDataException($"Reference catalog downgrade refused: installed {installed}, worker {catalog.CatalogVersion}. Restore a matching backup to roll back.");
            await CompanyCatalogImport.ValidateAsync(db, catalog, ct);
            await SystemCatalogImport.ValidateAsync(db, catalog, ct);
            await RegionCatalogImport.ValidateAsync(db, catalog, ct);
            await LanguageCatalogImport.ValidateAsync(db, catalog, ct);
            await RatingBoardCatalogImport.ValidateAsync(db, catalog, ct);
            await RatingCatalogImport.ValidateAsync(db, catalog, ct);
            await CatalogAliasReconciliation.ValidateAsync(db, catalog, ct);
            await CatalogAliasReconciliation.PruneAsync(db, catalog, ct);
            var assets = await CatalogArtworkImport.ImportAsync(db, blobs, catalog, ct);
            await CompanyCatalogImport.ImportAsync(db, catalog, ct);
            await RatingBoardCatalogImport.ImportAsync(db, catalog, ct);
            await SystemCatalogImport.ImportAsync(db, catalog, assets, ct);
            await RegionCatalogImport.ImportAsync(db, catalog, ct);
            await LanguageCatalogImport.ImportAsync(db, catalog, ct);
            await RatingCatalogImport.ImportAsync(db, catalog, assets, ct);
            await ReferenceCatalogPublisher.PublishAsync(db, catalog.CatalogVersion, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }
    internal static void ValidateIdentity(string resource, string key, ReferenceOwnership? ownership, bool present)
    {
        if (ownership == ReferenceOwnership.Romd && !present) throw new InvalidDataException($"Missing built-in {resource}/{key}; explicitly retire it instead.");
        if (ownership != ReferenceOwnership.Romd && present) throw new InvalidDataException($"Reference identity conflict: {resource}/{key}.");
    }
}
