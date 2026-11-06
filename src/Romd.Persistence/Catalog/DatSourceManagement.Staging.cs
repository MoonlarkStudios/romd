using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence.Catalog;

public sealed partial class DatSourceManagement
{
    public async Task<IReadOnlyList<string>> ReadRelatedCatalogIdsAsync(int sourceId, CancellationToken ct)
    {
        RequireMutationTransaction();
        return (await RelatedSubscriptions(sourceId).Select(row => row.CatalogId).Distinct().OrderBy(id => id).ToListAsync(ct)).AsReadOnly();
    }

    public Task<bool> TryLockCatalogAsync(string catalogId, CancellationToken ct)
    {
        RequireMutationTransaction();
        return db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(146, hashtext({catalogId})) AS \"Value\"").SingleAsync(ct);
    }

    public Task<bool> TryLockSourceAsync(int sourceId, CancellationToken ct)
    {
        RequireMutationTransaction();
        return db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(145, {sourceId}) AS \"Value\"").SingleAsync(ct);
    }

    public Task LockAffectedTitlesAsync(int sourceId, CancellationToken ct)
    {
        RequireMutationTransaction();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT t."Id" FROM romd."Titles" t
            WHERE EXISTS (SELECT 1 FROM romd."TitleSourceLinks" l
                JOIN romd."SourceEntries" e ON e."Id" = l."SourceEntryId"
                JOIN romd."DatSources" s ON s."CatalogSourceId" = e."CatalogSourceId"
                WHERE l."TitleId" = t."Id" AND s."Id" = {sourceId})
            ORDER BY t."Id" FOR UPDATE OF t
            """, ct);
    }

    public async Task<IReadOnlyList<int>> ReadVersionIdsAsync(int sourceId, CancellationToken ct)
    {
        RequireMutationTransaction();
        return (await db.DatFiles.Where(row => row.DatSourceId == sourceId).Select(row => row.Id).ToListAsync(ct)).AsReadOnly();
    }

    public Task BindRelatedSubscriptionsStagedAsync(int sourceId, CancellationToken ct)
    {
        RequireMutationTransaction();
        return RelatedSubscriptions(sourceId).Where(row => row.DatSourceId == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.DatSourceId, sourceId), ct);
    }

    private void RequireMutationTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Source lifecycle operations require the caller's transaction.");
    }
}
