using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;

namespace Romd.Persistence.ReferenceData;

/// <summary>Coordinates typed imports and publishes one atomic effective catalog.</summary>
internal sealed class RomdCatalogImporter
{
    internal static async Task ImportAsync(RomdDbContext db, IReferenceBlobStore blobs, RomdCatalogInput catalog, TimeProvider clock, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        IStagedReferencePublication? publication = null;
        Exception? primaryFailure = null;
        try
        {
            await ReferenceCatalogService.LockAsync(db, ct);
            var installed = await db.ReferenceCatalogState.Select(x => (int?)x.BuiltInVersion).SingleOrDefaultAsync(ct);
            if (installed > catalog.CatalogVersion)
                throw new InvalidDataException($"Reference catalog downgrade refused: installed {installed}, worker {catalog.CatalogVersion}. Restore a matching backup to roll back.");
            await CompanyCatalogImport.ValidateAsync(db, catalog, ct);
            await SystemCatalogImport.ValidateAsync(db, catalog, ct);
            await RegionCatalogImport.ValidateAsync(db, catalog, ct);
            await LanguageCatalogImport.ValidateAsync(db, catalog, ct);
            await RatingBoardCatalogImport.ValidateAsync(db, catalog, ct);
            await RatingCatalogImport.ValidateAsync(db, catalog, ct);
            await CatalogAliasReconciliation.ValidateAsync(db, catalog, ct);
            await CatalogAliasReconciliation.PruneAsync(db, catalog, ct);
            var icons = await CatalogArtworkImport.ReadPlanAsync(catalog, ct);
            // One batch retains input positions and acquires every icon hash lock in global order.
            publication = await blobs.PublishStagedAsync(icons.Select(icon => icon.Content.ToArray()).ToArray(), ct);
            if (publication.RequiresFlush)
                await db.SaveChangesAsync(ct);
            var fileIds = publication.ReadAfterFlush();
            var consumed = publication;
            publication = null;
            // Preserve the legacy failure boundary: dispose local staging before references are recorded.
            await consumed.DisposeAsync();
            var assets = await CatalogArtworkImport.ImportAsync(db, icons, fileIds, ct);
            await CompanyCatalogImport.ImportAsync(db, catalog, ct);
            await RatingBoardCatalogImport.ImportAsync(db, catalog, ct);
            await SystemCatalogImport.ImportAsync(db, catalog, assets, clock, ct);
            await RegionCatalogImport.ImportAsync(db, catalog, ct);
            await LanguageCatalogImport.ImportAsync(db, catalog, ct);
            await RatingCatalogImport.ImportAsync(db, catalog, assets, ct);
            await ReferenceCatalogPublisher.PublishAsync(db, catalog.CatalogVersion, ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception primary)
        {
            primaryFailure = primary;
            if (publication is { } owned)
            {
                publication = null;
                try
                { await owned.DisposeAsync(); }
                catch (Exception cleanup) { primary.Data["Romd.ReferencePublication.CleanupFailure"] = cleanup; }
            }
            try
            { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { primary.Data["Romd.ReferenceCatalog.RollbackFailure"] = rollback; }
            finally { db.ChangeTracker.Clear(); }
            throw;
        }
        finally
        {
            try
            { await transaction.DisposeAsync(); }
            catch (Exception disposal) when (primaryFailure is not null)
            { primaryFailure.Data["Romd.ReferenceCatalog.TransactionDisposalFailure"] = disposal; }
        }
    }
    internal static void ValidateIdentity(string resource, string key, ReferenceOwnership? ownership, bool present)
    {
        if (ownership == ReferenceOwnership.Romd && !present)
            throw new InvalidDataException($"Missing built-in {resource}/{key}; explicitly retire it instead.");
        if (ownership != ReferenceOwnership.Romd && present)
            throw new InvalidDataException($"Reference identity conflict: {resource}/{key}.");
    }
}
