using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Storage.Files;

namespace Romd.Persistence.MediaFacts;

public sealed class MediaRepresentationCleanup(RomdDbContext context) : IMediaRepresentationCleanup
{
    public async Task<int> CollectAsync(int maximum, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 256);
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Representation collection owns its bounded cleanup transactions.");
        int[] candidates = await context.Database.SqlQueryRaw<int>("""
            SELECT r."Id" AS "Value" FROM romd."Representations" r WHERE
                NOT EXISTS (SELECT 1 FROM romd."RepresentationOutputs" WHERE "RepresentationId" = r."Id" AND "RomId" IS NOT NULL) AND
                NOT EXISTS (SELECT 1 FROM romd."CdArchiveOwners" WHERE "RepresentationId" = r."Id") AND
                NOT EXISTS (SELECT 1 FROM romd."CatalogCdLayoutBindings" WHERE "RepresentationId" = r."Id") AND
                NOT EXISTS (SELECT 1 FROM romd."OriginalDiscLayoutFiles" WHERE "FileId" = r."FileId") AND
                NOT EXISTS (SELECT 1 FROM romd."MediaReconciliationPins" WHERE "RepresentationId" = r."Id")
            ORDER BY r."Id" LIMIT @maximum
            """, new NpgsqlParameter("maximum", maximum)).ToArrayAsync(ct);
        int collected = 0;
        foreach (int id in candidates)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            bool removed = await context.Database.SqlQuery<bool>($"""SELECT romd.delete_unowned_media_representation({id}) AS "Value" """).SingleAsync(ct);
            await transaction.CommitAsync(ct);
            if (removed)
                collected++;
        }
        return collected;
    }
}
