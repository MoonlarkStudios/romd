using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Persistence.MetadataProviders;

namespace Romd.Persistence.Catalog;

public sealed class DatTitleGroupingReader(RomdDbContext db) : IDatTitleGroupingReader
{
    public async Task<DatTitleGroupingEvidence?> ReadAsync(int cloneTitleId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
            return await ReadCurrentAsync(cloneTitleId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var evidence = await ReadCurrentAsync(cloneTitleId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return evidence;
    }

    private async Task<DatTitleGroupingEvidence?> ReadCurrentAsync(int cloneTitleId, CancellationToken cancellationToken)
    {
        var family = await HashBackedDatFamilyReader.ReadAsync(db, cloneTitleId, cancellationToken);
        if (family?.Parent is not { } parent)
            return null;

        var match = await (from clone in db.Titles
                           from target in db.Titles
                           from dat in db.DatFiles
                           where clone.Id == cloneTitleId && target.Id != clone.Id && target.PlatformId == clone.PlatformId
                                 && dat.Id == parent.DatFileId
                                 && target.Id == ProviderIdentityDatabaseFunctions.DatParentTitle(parent.DatFileId, parent.GameId)
                           select new
                           {
                               target.Id,
                               target.Name,
                               ParentRevision = target.Revision,
                               CloneRevision = clone.Revision,
                               DatName = dat.Name,
                               dat.Version,
                               CloneEvidence = ProviderIdentityDatabaseFunctions.Fingerprint(clone.Id, ProviderIdentityPolicy.Version),
                               ParentEvidence = ProviderIdentityDatabaseFunctions.Fingerprint(target.Id, ProviderIdentityPolicy.Version)
                           }).SingleOrDefaultAsync(cancellationToken);
        if (match is null)
            return null;

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{cloneTitleId}:{match.Id}:{parent.DatFileId}:{parent.GameId}:{match.CloneEvidence}:{match.ParentEvidence}")));
        return new(match.Id, match.Name, match.ParentRevision, match.CloneRevision, match.DatName, match.Version, family.Names, fingerprint);
    }
}
