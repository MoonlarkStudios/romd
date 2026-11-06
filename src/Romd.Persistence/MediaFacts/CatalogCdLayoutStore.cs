using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Source.Rom;

namespace Romd.Persistence.MediaFacts;

public sealed class CatalogCdLayoutStore(RomdDbContext db) : ICatalogCdLayoutStore
{
    public async Task<bool> LockScopeAsync(CatalogDescriptorRequirement requirement, CancellationToken ct)
    {
        RequireTransaction(ct);
        ArgumentNullException.ThrowIfNull(requirement);
        await CatalogTopologyFence.AcquireAsync(db, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM romd."CatalogReleases" WHERE "Id" = {requirement.ReleaseId} FOR NO KEY UPDATE""", ct);
        return await db.CatalogReleaseFiles.AnyAsync(file => file.Id == requirement.CatalogFileId && file.CatalogReleaseId == requirement.ReleaseId &&
            file.FileFingerprint == requirement.FileFingerprint && file.Name == requirement.Path && file.Size == requirement.Size &&
            file.Sha1 == requirement.Sha1 && file.Md5 == requirement.Md5 && file.Crc == requirement.Crc32 &&
            db.CatalogReleases.Any(release => release.Id == file.CatalogReleaseId && release.Fingerprint == requirement.ReleaseFingerprint && !release.HasTitleConflict), ct);
    }

    public async Task<ErrorOr<int>> PublishAsync(CatalogCdLayoutPublication publication, CancellationToken ct)
    {
        RequireTransaction(ct);
        ArgumentNullException.ThrowIfNull(publication);
        var required = publication.Requirement;
        if (!await LockScopeAsync(required, ct))
            return Error.Conflict("CatalogCdLayout.CatalogChanged", "The captured descriptor requirement changed.");
        var transaction = db.Database.CurrentTransaction!;
        const string savepoint = "catalog_cd_layout_publication";
        await transaction.CreateSavepointAsync(savepoint, ct);
        try
        {
            var published = await PublishCoreAsync(publication, ct);
            await transaction.ReleaseSavepointAsync(savepoint, ct);
            return published;
        }
        catch (Exception error)
        {
            try
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            }
            catch (Exception recovery)
            {
                if (error is not OperationCanceledException)
                {
                    recovery.Data["Romd.CatalogCdLayout.PublicationFailure"] = error;
                    throw;
                }
                error.Data["Romd.CatalogCdLayout.SavepointRecoveryFailure"] = recovery;
            }
            if (error is PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "catalog_cd_layout_original_changed" })
                return CatalogCdLayoutErrors.SourceChanged;
            throw;
        }
    }

    private async Task<ErrorOr<int>> PublishCoreAsync(CatalogCdLayoutPublication publication, CancellationToken ct)
    {
        var required = publication.Requirement;
        int[] inserted = await db.Database.SqlQuery<int>($"""
            INSERT INTO romd."CatalogCdLayoutBindings" ("Revision","CatalogFileId","CatalogReleaseId","CatalogFingerprint","RequirementFingerprint","Path",
                "ExpectedSize","ExpectedSha1","ExpectedMd5","ExpectedCrc32","RepresentationId","MemberOrdinal","GeneratorProfile","CreatedAtUtcTicks")
            VALUES ({publication.Revision},{required.CatalogFileId},{required.ReleaseId},{required.ReleaseFingerprint},{required.FileFingerprint},{required.Path},
                {required.Size},{required.Sha1.ToArray()},{required.Md5.ToArray()},{required.Crc32.ToArray()},{publication.RepresentationId},
                {publication.MemberOrdinal},{publication.GeneratorProfile},{publication.CreatedAt.UtcTicks})
            ON CONFLICT ("CatalogFileId") DO NOTHING RETURNING "Id" AS "Value"
            """).ToArrayAsync(ct);
        if (inserted.Length == 1)
            return inserted[0];
        int[] existing = await db.Database.SqlQuery<int>($"""
            SELECT "Id" AS "Value" FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId" = {required.CatalogFileId} AND
                "CatalogReleaseId" = {required.ReleaseId} AND "CatalogFingerprint" = {required.ReleaseFingerprint} AND
                "RequirementFingerprint" = {required.FileFingerprint} AND "Path" = {required.Path} AND "ExpectedSize" = {required.Size} AND
                "ExpectedSha1" = {required.Sha1.ToArray()} AND "ExpectedMd5" = {required.Md5.ToArray()} AND "ExpectedCrc32" = {required.Crc32.ToArray()} AND
                "RepresentationId" = {publication.RepresentationId} AND "MemberOrdinal" = {publication.MemberOrdinal} AND "GeneratorProfile" = {publication.GeneratorProfile}
            """).ToArrayAsync(ct);
        return existing.Length == 1 ? existing[0]
            : Error.Conflict("CatalogCdLayout.BindingChanged", "Withdraw the existing descriptor revision before publishing a replacement.");
    }

    public async Task<bool> WithdrawAsync(int id, Guid revision, CancellationToken ct)
    {
        RequireTransaction(ct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        if (revision == Guid.Empty)
            throw new ArgumentException("The exact binding revision is required.", nameof(revision));
        await CatalogTopologyFence.AcquireAsync(db, ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM romd."CatalogCdLayoutBindings" WHERE "Id" = {id} AND "Revision" = {revision}""", ct) == 1;
    }

    private void RequireTransaction(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Catalog CD layout publication requires the caller's ReadCommitted transaction.");
    }
}
