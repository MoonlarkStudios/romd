using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Collections;

namespace Romd.Persistence.Queries;

public sealed class CollectionCoverMediaReader(RomdDbContext context) : ICollectionCoverMediaReader
{
    public async Task<bool> LockStagedAsync(int mediaId, CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Collection cover validation requires the caller's transaction.");

        var rows = await context.Database.SqlQuery<int>(
                $"""SELECT "Id" AS "Value" FROM romd."TitleMedia" WHERE "Id" = {mediaId} FOR KEY SHARE""")
            .ToListAsync(ct);
        return rows.Count != 0;
    }
}
