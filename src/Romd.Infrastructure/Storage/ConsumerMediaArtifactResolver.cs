using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Delivery;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class ConsumerMediaArtifactResolver(
    RomdDbContext context,
    IContentAddressableStore cas) : IConsumerMediaArtifactResolver
{
    public async Task<ConsumerMediaArtifact?> ResolveMediaAsync(
        ConsumerMediaArtifactRequest request,
        CancellationToken ct = default)
    {
        var media = await context.TitleMedia
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.MediaId)
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

        if (media is null)
        {
            return null;
        }

        var stream = await cas.RetrieveAsync(StorageKey.FromHash(media.Sha256), ct);

        return stream is null
            ? null
            : new ConsumerMediaArtifact(stream, media.ContentType);
    }
}
