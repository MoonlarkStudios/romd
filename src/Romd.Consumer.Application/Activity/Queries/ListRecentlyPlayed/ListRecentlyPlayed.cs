using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Activity;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.Activity.Queries.ListRecentlyPlayed;

public sealed record ListRecentlyPlayedQuery(int Limit, string? Cursor = null) : IQuery<Page<RecentlyPlayedTitleDto>>
{
    /// <summary>The largest page the operation serves; the endpoint publishes and enforces it.</summary>
    public const int MaxLimit = 50;
}

public sealed class ListRecentlyPlayedQueryHandler(ICurrentUser currentUser, IPlayActivityRepository repository)
    : IQueryHandler<ListRecentlyPlayedQuery, Page<RecentlyPlayedTitleDto>>
{
    public async Task<ErrorOr<Page<RecentlyPlayedTitleDto>>> HandleAsync(
        ListRecentlyPlayedQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        if (query.Limit is < 1 or > ListRecentlyPlayedQuery.MaxLimit)
        {
            return ConsumerErrors.InvalidPlaySession($"limit must be between 1 and {ListRecentlyPlayedQuery.MaxLimit}.");
        }

        var result = await repository.ListRecentlyPlayedPageAsync(userId, query.Cursor, query.Limit, ct);
        return result switch
        {
            ConsumerLibraryReadResult<PagedList<RecentlyPlayedData>>.Found found => new Page<RecentlyPlayedTitleDto>
            {
                Items = found.Value.Items.Select(PlayActivityMapping.ToContract).ToArray(),
                NextCursor = found.Value.NextCursor,
                HasNextPage = found.Value.HasNextPage
            },
            ConsumerLibraryReadResult<PagedList<RecentlyPlayedData>>.Rejected rejected => rejected.Error,
            _ => ConsumerErrors.CurrentLibraryUnavailable()
        };
    }
}
