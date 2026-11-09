using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Collections;
using Romd.Contracts.Consumer.Libraries;

namespace Romd.Consumer.Application.Libraries.Queries.GetCurrentLibraryContext;

public sealed record GetCurrentLibraryContextQuery : IQuery<LibraryContext>;

public sealed class GetCurrentLibraryContextQueryHandler(
    ICurrentUser currentUser,
    IConsumerLibraryContextRepository libraryContextRepository)
    : IQueryHandler<GetCurrentLibraryContextQuery, LibraryContext>
{
    public async Task<ErrorOr<LibraryContext>> HandleAsync(
        GetCurrentLibraryContextQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var context = await libraryContextRepository.GetCurrentContextAsync(
            new ConsumerLibraryScope(userId),
            ct);
        return context switch
        {
            ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.Found found => ToContract(found.Value),
            ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.ItemNotFound =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.ProjectionInconsistent =>
                ConsumerErrors.LibraryNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library context result.")
        };
    }

    private static LibraryContext ToContract(ConsumerLibraryContextReadModel context) =>
        new()
        {
            Name = context.Name,
            Counts = new LibraryContentCounts
            {
                OwnedTitleCount = context.Counts.OwnedTitleCount,
                AvailableTitleCount = context.Counts.AvailableTitleCount,
                CollectionCount = context.Counts.CollectionCount
            },
            Platforms = context.Platforms
                .Select(facet => new LibraryFacet(
                    facet.SystemKey,
                    facet.PlatformName,
                    facet.Count))
                .ToList(),
            Genres = context.Genres
                .Select(facet => new LibraryFacet(facet.Genre, facet.Genre, facet.Count))
                .ToList(),
            FeaturedCollections = context.FeaturedCollections
                .Select(facet => new ConsumerCollection
                {
                    Id = IdCoder.Encode(facet.CollectionId),
                    Name = facet.CollectionName,
                    ItemCount = facet.MatchingCount
                })
                .ToList()
        };
}
