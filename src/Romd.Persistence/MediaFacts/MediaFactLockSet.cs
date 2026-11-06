using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

/// <summary>Locks complete fact authority in ROM, representation and artifact/dependency order before a protected ReadCommitted read.</summary>
internal static class MediaFactLockSet
{
    internal static async Task<IReadOnlyDictionary<int, RomRepresentationFacts>> ReadAsync(RomdDbContext context, int[] representationIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Media observation publication requires the caller's ReadCommitted transaction.");
        foreach (int id in representationIds)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        int[] ids = representationIds.Distinct().Order().ToArray();
        await context.Database.ExecuteSqlRawAsync("""
            SELECT 1 FROM romd."Roms" WHERE "Id" IN (SELECT "RomId" FROM romd."RepresentationOutputs" WHERE "RepresentationId" = ANY (@ids))
                ORDER BY "Id" FOR NO KEY UPDATE
            """, [new NpgsqlParameter("ids", ids)], ct);
        // Execute the existing sorted per-representation lock sequence in bounded commands.
        // Parameterized statements preserve the lock order without one round trip per representation.
        foreach (int[] batch in ids.Chunk(IMediaFactReader.MaximumBatchSize))
        {
            string sql = string.Join("\n", batch.Select((_, index) => $"""
                SELECT pg_advisory_xact_lock(9415,@representation{index});
                SELECT 1 FROM romd."Representations" WHERE "Id" = @representation{index} FOR NO KEY UPDATE;
                """));
            var parameters = batch.Select((id, index) => new NpgsqlParameter($"representation{index}", id)).ToArray();
            await context.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
        await context.Database.ExecuteSqlRawAsync("""
            SELECT 1 FROM romd."Files" WHERE "Id" IN (
                SELECT "FileId" FROM romd."Representations" WHERE "Id" = ANY (@ids)
                UNION SELECT "StoredFileId" FROM romd."ReconstructionMembers" WHERE "RepresentationId" = ANY (@ids) AND "StoredFileId" IS NOT NULL)
            ORDER BY "Id" FOR NO KEY UPDATE
            """, [new NpgsqlParameter("ids", ids)], ct);
        var result = new Dictionary<int, RomRepresentationFacts>();
        foreach (int[] batch in ids.Chunk(IMediaFactReader.MaximumBatchSize))
            foreach (var row in await new MediaFactReader(context).ReadLockedPublicationAsync(batch, ct))
                result.Add(row.Key, row.Value);
        return result;
    }
}
