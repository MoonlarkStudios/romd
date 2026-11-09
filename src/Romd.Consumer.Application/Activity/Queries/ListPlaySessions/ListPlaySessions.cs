using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Activity;

namespace Romd.Consumer.Application.Activity.Queries.ListPlaySessions;

public sealed record ListPlaySessionsQuery(string? Cursor, int Limit) : IQuery<Page<PlaySession>>
{
    /// <summary>The largest page the operation serves; the endpoint publishes and enforces it.</summary>
    public const int MaxLimit = 100;
}

public sealed class ListPlaySessionsQueryHandler(ICurrentUser currentUser, IPlayActivityRepository repository)
    : IQueryHandler<ListPlaySessionsQuery, Page<PlaySession>>
{
    public async Task<ErrorOr<Page<PlaySession>>> HandleAsync(
        ListPlaySessionsQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        if (query.Limit is < 1 or > ListPlaySessionsQuery.MaxLimit)
        {
            return ConsumerErrors.InvalidPlaySession($"limit must be between 1 and {ListPlaySessionsQuery.MaxLimit}.");
        }

        var page = await repository.ListAccessibleAsync(userId, query.Cursor, query.Limit, ct);
        return page is null ? ConsumerErrors.InvalidCursor() : page.ToContract();
    }
}
