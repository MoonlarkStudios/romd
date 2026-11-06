using System.Data;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;

namespace Romd.Persistence.Repositories;

/// <summary>Captured descriptor scope and physical identity under an independent read-only snapshot.</summary>
public sealed class ConsumerDerivedDescriptorSource : IConsumerDerivedDescriptorSource
{
    private readonly DbContextOptions<RomdDbContext> _readOptions;
    public ConsumerDerivedDescriptorSource(DbContextOptions<RomdDbContext> readOptions) => this._readOptions = readOptions;
    public async Task<bool> IsRetainedAsync(DerivedDescriptorBinding captured, CancellationToken ct = default)
    {
        if (!ConsumerDerivedDescriptors.IsValid(captured))
            return false;
        await using var db = new RomdDbContext(_readOptions);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var identity = captured.Identity;
        bool retained = await db.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM romd."CurrentCatalogCdLayoutBindings" binding
            JOIN romd."ReconstructionMembers" member ON member."RepresentationId"=binding."RepresentationId" AND member."Ordinal"=binding."MemberOrdinal"
            WHERE binding."Id"={captured.Id} AND binding."Revision"={captured.Revision} AND binding."CatalogReleaseId"={captured.ReleaseId}
                AND binding."CatalogFileId"={captured.CatalogFileId} AND binding."RepresentationId"={captured.RepresentationId}
                AND binding."MemberOrdinal"={captured.MemberOrdinal} AND binding."Path"={captured.RelativePath}
                AND binding."GeneratorProfile"={captured.GeneratorProfile} AND member."StoredRole"='Cue' AND member."StoredFileId"={captured.FileId}
                AND member."ContentSha256"={identity.Sha256.ToArray()} AND member."ContentSize"={identity.Size}
                AND member."Sha1"={identity.Sha1.ToArray()} AND member."Md5"={identity.Md5.ToArray()} AND member."Crc32"={identity.Crc32.ToArray()}
                AND (member."ContentSize",member."Sha1",member."Md5",member."Crc32") <>
                    (binding."ExpectedSize",binding."ExpectedSha1",binding."ExpectedMd5",binding."ExpectedCrc32")
            """).AnyAsync(ct);
        await snapshot.CommitAsync(ct);
        return retained;
    }
}
