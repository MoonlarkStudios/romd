using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Artwork.Commands.QueueArtworkCompletion;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Persistence;
using Romd.Persistence.Artwork;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>Existing read/worker fixtures accept runs through the real Application owner and the same scoped context.</summary>
internal static class ArtworkCompletionTestServices
{
    internal static Task<ErrorOr<Guid>> AcceptCompletionAsync(this ArtworkCompletionStore store, RomdDbContext db,
        ArtworkCompletionRequest request, Guid actor, CancellationToken ct) =>
        new QueueArtworkCompletionCommandHandler(store, new EfUnitOfWork(db), TimeProvider.System,
            NullLogger<QueueArtworkCompletionCommandHandler>.Instance).HandleAsync(new(request, actor), ct);

    internal static Task<ErrorOr<Guid>> AcceptCompletionAsync(this ArtworkCompletionStore store, RomdDbContext db,
        Guid requestId, IReadOnlyList<int> titleIds, Guid actor, CancellationToken ct) =>
        store.AcceptCompletionAsync(db, new(requestId, titleIds.Select(IdCoder.Encode).ToArray()), actor, ct);
}
