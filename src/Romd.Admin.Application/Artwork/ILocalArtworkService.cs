using ErrorOr;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork;

public interface ILocalArtworkService
{
    Task<ErrorOr<long>> PinMediaAsync(int titleId, ArtworkRole role, int mediaId, long expectedRevision,
        Guid actorId, CancellationToken ct = default, int focalX = 50, int focalY = 50);
}
