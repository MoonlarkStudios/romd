using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles.Queries.OpenTitleMedia;

namespace Romd.Persistence.Queries;

public sealed class TitleMediaFileReader(RomdDbContext db) : ITitleMediaFileReader
{
    public Task<TitleMediaFile?> FindAsync(int mediaId, CancellationToken ct) =>
        db.TitleMedia
            .AsNoTracking()
            .Where(media => media.Id == mediaId)
            .Select(media => new TitleMediaFile(media.FileId, media.ContentType))
            .FirstOrDefaultAsync(ct);
}
