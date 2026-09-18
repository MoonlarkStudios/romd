using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Activity;

namespace Romd.Consumer.Application.Activity.Queries.ListPlaySessions;

public sealed record ListPlaySessionsQuery(string? Cursor, int Limit) : IQuery<Page<PlaySessionDto>>;

public sealed class ListPlaySessionsQueryHandler(ICurrentUser currentUser, IPlayActivityRepository repository)
    : IQueryHandler<ListPlaySessionsQuery, Page<PlaySessionDto>>
{
    public async Task<ErrorOr<Page<PlaySessionDto>>> HandleAsync(
        ListPlaySessionsQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        if (query.Limit is < 1 or > 100)
        {
            return ConsumerErrors.InvalidPlaySession("limit must be between 1 and 100.");
        }

        var page = await repository.ListAccessibleAsync(userId, query.Cursor, query.Limit, ct);
        return page is null ? ConsumerErrors.InvalidCursor() : page.ToContract();
    }
}
