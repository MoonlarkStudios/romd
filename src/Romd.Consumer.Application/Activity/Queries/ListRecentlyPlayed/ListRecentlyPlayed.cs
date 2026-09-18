using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Activity;

namespace Romd.Consumer.Application.Activity.Queries.ListRecentlyPlayed;

public sealed record ListRecentlyPlayedQuery(int Limit) : IQuery<IReadOnlyList<RecentlyPlayedTitleDto>>;

public sealed class ListRecentlyPlayedQueryHandler(ICurrentUser currentUser, IPlayActivityRepository repository)
    : IQueryHandler<ListRecentlyPlayedQuery, IReadOnlyList<RecentlyPlayedTitleDto>>
{
    public async Task<ErrorOr<IReadOnlyList<RecentlyPlayedTitleDto>>> HandleAsync(
        ListRecentlyPlayedQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        if (query.Limit is < 1 or > 50)
        {
            return ConsumerErrors.InvalidPlaySession("limit must be between 1 and 50.");
        }

        var items = await repository.ListRecentlyPlayedAsync(userId, query.Limit, ct);
        return items is null
            ? ConsumerErrors.CurrentLibraryUnavailable()
            : items.Select(PlayActivityMapping.ToContract).ToArray();
    }
}
