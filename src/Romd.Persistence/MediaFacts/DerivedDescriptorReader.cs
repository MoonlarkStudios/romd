using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

public sealed class DerivedDescriptorReader(RomdDbContext db) : IDerivedDescriptorReader
{
    public async Task<IReadOnlyList<DerivedDescriptorBinding>> ReadForReleaseAsync(int releaseId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(releaseId);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Derived descriptor reads require the caller's consistent authorization snapshot.");
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT d."Id",d."Revision",d."CatalogReleaseId" AS "ReleaseId",d."CatalogFileId",d."Path",d."RepresentationId",d."MemberOrdinal",
                m."StoredFileId" AS "FileId",m."ContentSha256",m."ContentSize",m."Sha1",m."Md5",m."Crc32",d."GeneratorProfile"
            FROM romd."CurrentCatalogCdLayoutBindings" d JOIN romd."CatalogReleases" r ON r."Id" = d."CatalogReleaseId"
            JOIN romd."CatalogReleaseFiles" f ON f."Id" = d."CatalogFileId" AND f."CatalogReleaseId" = r."Id"
            JOIN romd."ReconstructionMembers" m ON m."RepresentationId" = d."RepresentationId" AND m."Ordinal" = d."MemberOrdinal"
            WHERE r."Id" = {releaseId} AND
                (m."ContentSize",m."Sha1",m."Md5",m."Crc32") <> (d."ExpectedSize",d."ExpectedSha1",d."ExpectedMd5",d."ExpectedCrc32") AND
                NOT EXISTS (SELECT 1 FROM romd."Roms" original JOIN romd."RepresentationOutputs" output ON output."Id" = original."PreferredOutputId" AND output."RomId" = original."Id"
                    JOIN romd."Representations" representation ON representation."Id" = output."RepresentationId"
                    WHERE representation."Encoding" = 'Identity' AND EXISTS (SELECT 1 FROM romd."Verifications" WHERE "RepresentationId" = representation."Id") AND
                        (original."ContentSize",original."Sha1",original."Md5",original."Crc32") = (f."Size",f."Sha1",f."Md5",f."Crc"))
            ORDER BY d."Id"
            """).ToArrayAsync(ct);
        return rows.Select(row => new DerivedDescriptorBinding(row.Id, row.Revision, row.ReleaseId, row.CatalogFileId, row.Path,
            row.RepresentationId, row.MemberOrdinal, row.FileId, new RomLogicalIdentity(row.ContentSize, Sha256.FromSpan(row.ContentSha256),
                Sha1.FromSpan(row.Sha1), Md5.FromSpan(row.Md5), Crc32.FromSpan(row.Crc32)), row.GeneratorProfile)).ToArray();
    }
    private sealed record Row(int Id, Guid Revision, int ReleaseId, int CatalogFileId, string Path, int RepresentationId, int MemberOrdinal,
        int FileId, byte[] ContentSha256, long ContentSize, byte[] Sha1, byte[] Md5, byte[] Crc32, string GeneratorProfile);
}
