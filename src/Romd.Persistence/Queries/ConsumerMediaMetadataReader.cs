using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Delivery;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Queries;

public sealed class ConsumerMediaMetadataReader(RomdDbContext context) : IConsumerMediaMetadataReader
{
    public async Task<ConsumerMediaMetadata?> FindAsync(int mediaId, CancellationToken ct = default)
    {
        var media = await context.TitleMedia
            .AsNoTracking()
            .Where(candidate => candidate.Id == mediaId)
            .Join(
                context.Set<FileEntityPersistence>(),
                media => media.FileId,
                file => file.Id,
                (media, file) => new
                {
                    media.ContentType,
                    file.Sha256
                })
            .FirstOrDefaultAsync(ct);

        return media is null ? null : new ConsumerMediaMetadata(media.Sha256, media.ContentType);
    }
}
