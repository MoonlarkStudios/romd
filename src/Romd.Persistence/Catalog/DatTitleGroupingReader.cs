using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Persistence.MetadataProviders;

namespace Romd.Persistence.Catalog;

public sealed class DatTitleGroupingReader(RomdDbContext db) : IDatTitleGroupingReader
{
    public async Task<DatTitleGroupingRead?> ReadAsync(int cloneTitleId, CancellationToken cancellationToken)
    {
        if (!await db.Titles.AnyAsync(title => title.Id == cloneTitleId, cancellationToken))
            return null;

        var family = await HashBackedDatFamilyReader.ReadAsync(db, cloneTitleId, cancellationToken);
        if (family?.Parent is not { } parent)
            return new(null);

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
            return new(null);

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{cloneTitleId}:{match.Id}:{parent.DatFileId}:{parent.GameId}:{match.CloneEvidence}:{match.ParentEvidence}")));
        return new(new(match.Id, match.Name, match.ParentRevision, match.CloneRevision, match.DatName, match.Version, family.Names, fingerprint));
    }
}
