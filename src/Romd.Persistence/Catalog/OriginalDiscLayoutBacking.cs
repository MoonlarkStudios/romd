using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Persistence.Queries;
using Romd.Persistence.Repositories;

namespace Romd.Persistence.Catalog;

/// <summary>Layout paths identify logical originals even when their physical owner is a verified CD archive.</summary>
internal static class OriginalDiscLayoutBacking
{
    internal static async Task<IReadOnlyList<OriginalRomContentRow>> ReadAsync(RomdDbContext db, int[] romIds, CancellationToken ct)
    {
        var result = await db.OriginalRomContent().Where(x => romIds.Contains(x.RomId)).ToListAsync(ct);
        var cd = from artifact in db.RomArtifacts()
                 join rom in db.RomFiles on artifact.RomId equals rom.Id
                 where romIds.Contains(rom.Id) && artifact.MemberOrdinal != null && artifact.HasMeasuredEvidence
                 select new OriginalRomContentRow { RepresentationId = artifact.RepresentationId, OutputId = artifact.OutputId, RomId = rom.Id, FileId = artifact.FileId, Sha256 = rom.ContentSha256, Sha1 = rom.Sha1, Size = rom.ContentSize };
        result.AddRange(await cd.ToListAsync(ct));
        return result;
    }
}
