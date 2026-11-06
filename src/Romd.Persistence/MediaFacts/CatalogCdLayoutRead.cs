using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

/// <summary>Current immutable binding identity. Callers own a consistent snapshot or the complete publication lock set.</summary>
internal sealed record CatalogCdLayoutSnapshot(int Id, Guid Revision, int PlatformId, CatalogDescriptorRequirement Requirement,
    int RepresentationId, int MemberOrdinal, string GeneratorProfile);

internal static class CatalogCdLayoutRead
{
    internal static async Task<CatalogCdLayoutSnapshot?> ReadAsync(RomdDbContext db, int catalogFileId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Catalog CD source reads require the caller's consistent transaction.");
        var row = await db.Database.SqlQuery<Row>($"""
            SELECT "Id","Revision","PlatformId","CatalogFileId","CatalogReleaseId","CatalogFingerprint","RequirementFingerprint","Path",
                "ExpectedSize","ExpectedSha1","ExpectedMd5","ExpectedCrc32","RepresentationId","MemberOrdinal","GeneratorProfile"
            FROM romd."CurrentCatalogCdLayoutBindings" WHERE "CatalogFileId"={catalogFileId}
            """).SingleOrDefaultAsync(ct);
        return row is null ? null : new(row.Id, row.Revision, row.PlatformId,
            new(row.CatalogFileId, row.CatalogReleaseId, row.CatalogFingerprint, row.RequirementFingerprint, row.Path, row.ExpectedSize,
                Sha1.FromSpan(row.ExpectedSha1), Md5.FromSpan(row.ExpectedMd5), Crc32.FromSpan(row.ExpectedCrc32)),
            row.RepresentationId, row.MemberOrdinal, row.GeneratorProfile);
    }
    internal static async Task<IReadOnlyList<CatalogCdLayoutSnapshot>> FindForReleaseAsync(RomdDbContext db, int releaseId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Candidate reads require the caller's consistent snapshot.");
        int[] ids = await db.Database.SqlQuery<int>($"""
            SELECT "CatalogFileId" AS "Value" FROM romd."CurrentCatalogCdLayoutBindings" WHERE "CatalogReleaseId"={releaseId}
            ORDER BY "CatalogFileId" LIMIT 2
            """).ToArrayAsync(ct);
        var result = new List<CatalogCdLayoutSnapshot>();
        foreach (int id in ids)
            if (await ReadAsync(db, id, ct) is { } binding)
                result.Add(binding);
        return result.AsReadOnly();
    }
    /// <summary>Retained factual ownership, including bindings whose logical outputs were withdrawn. Requires the caller's topology/catalog fence.</summary>
    internal static async Task<CatalogCdLayoutSnapshot?> ReadRetainedAsync(RomdDbContext db, int catalogFileId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Retained CD ownership requires the caller's transaction.");
        var row = await db.Database.SqlQuery<Row>($"""
            SELECT binding."Id",binding."Revision",release."PlatformId",binding."CatalogFileId",binding."CatalogReleaseId",binding."CatalogFingerprint",binding."RequirementFingerprint",binding."Path",
                binding."ExpectedSize",binding."ExpectedSha1",binding."ExpectedMd5",binding."ExpectedCrc32",binding."RepresentationId",binding."MemberOrdinal",binding."GeneratorProfile"
            FROM romd."CatalogCdLayoutBindings" binding JOIN romd."CatalogReleases" release ON release."Id"=binding."CatalogReleaseId"
            WHERE binding."CatalogFileId"={catalogFileId}
            """).SingleOrDefaultAsync(ct);
        return row is null ? null : new(row.Id, row.Revision, row.PlatformId,
            new(row.CatalogFileId, row.CatalogReleaseId, row.CatalogFingerprint, row.RequirementFingerprint, row.Path, row.ExpectedSize,
                Sha1.FromSpan(row.ExpectedSha1), Md5.FromSpan(row.ExpectedMd5), Crc32.FromSpan(row.ExpectedCrc32)),
            row.RepresentationId, row.MemberOrdinal, row.GeneratorProfile);
    }
    internal static async Task LockScopeAsync(RomdDbContext db, int catalogFileId, CancellationToken ct)
    {
        await CatalogTopologyFence.AcquireAsync(db, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM romd."CatalogReleases" WHERE "Id" IN (SELECT "CatalogReleaseId" FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId"={catalogFileId}) FOR NO KEY UPDATE;
            SELECT 1 FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId"={catalogFileId} FOR NO KEY UPDATE;
            """, ct);
    }
    internal static Task<int[]> RomIdsAsync(RomdDbContext db, int catalogFileId, CancellationToken ct) => db.Database.SqlQuery<int>($"""
        SELECT DISTINCT output."RomId" AS "Value" FROM romd."CatalogCdLayoutBindings" binding
        JOIN romd."RepresentationOutputs" output ON output."RepresentationId"=binding."RepresentationId"
        WHERE binding."CatalogFileId"={catalogFileId} AND output."RomId" IS NOT NULL
        """).ToArrayAsync(ct);
    internal static Task<int[]> RepresentationIdsAsync(RomdDbContext db, int catalogFileId, CancellationToken ct) => db.Database.SqlQuery<int>($"""
        SELECT "RepresentationId" AS "Value" FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId"={catalogFileId}
        """).ToArrayAsync(ct);
    internal static Task<bool> HasOwnerAsync(RomdDbContext db, int representationId, CancellationToken ct) => db.Database.SqlQuery<int>($"""
        SELECT 1 AS "Value" FROM romd."CatalogCdLayoutBindings" WHERE "RepresentationId"={representationId}
        """).AnyAsync(ct);
    internal static Task<bool> HasVerifiedOriginalAsync(RomdDbContext db, RomCdFileIdentity identity, CancellationToken ct) => db.Database.SqlQuery<int>($"""
        SELECT 1 AS "Value" FROM romd."Roms" original
        JOIN romd."RepresentationOutputs" output ON output."Id"=original."PreferredOutputId" AND output."RomId"=original."Id"
        JOIN romd."Representations" representation ON representation."Id"=output."RepresentationId"
        WHERE representation."Encoding"='Identity' AND EXISTS (SELECT 1 FROM romd."Verifications" WHERE "RepresentationId"=representation."Id") AND
            (original."ContentSha256",original."ContentSize",original."Sha1",original."Md5",original."Crc32")=
                ({identity.Sha256.ToArray()},{identity.Size},{identity.Sha1.ToArray()},{identity.Md5.ToArray()},{identity.Crc32.ToArray()})
        """).AnyAsync(ct);
    internal static bool Matches(CatalogCdLayoutSnapshot? actual, Romd.Admin.Application.Source.Rom.Reconciliation.MediaReconciliationLayout.Catalog expected) =>
        actual is not null && actual.Id == expected.BindingId && actual.Revision == expected.Revision && actual.PlatformId == expected.PlatformId &&
        actual.Requirement == expected.Requirement && actual.GeneratorProfile == expected.GeneratorProfile;

    private sealed record Row(int Id, Guid Revision, int PlatformId, int CatalogFileId, int CatalogReleaseId, string CatalogFingerprint,
        string RequirementFingerprint, string Path, long ExpectedSize, byte[] ExpectedSha1, byte[] ExpectedMd5, byte[] ExpectedCrc32,
        int RepresentationId, int MemberOrdinal, string GeneratorProfile);
}
