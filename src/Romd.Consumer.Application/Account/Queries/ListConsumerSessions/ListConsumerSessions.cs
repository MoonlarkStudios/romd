using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Account.Queries.ListConsumerSessions;

public sealed record ListConsumerSessionsQuery(Guid? CurrentSessionId, string? Cursor, int Limit)
    : IQuery<Page<ConsumerSignInSession>>;

public sealed class ListConsumerSessionsQueryHandler(ICurrentUser user, IAccountSessions sessions)
    : IQueryHandler<ListConsumerSessionsQuery, Page<ConsumerSignInSession>>
{
    public async Task<ErrorOr<Page<ConsumerSignInSession>>> HandleAsync(ListConsumerSessionsQuery query, CancellationToken ct = default)
    {
        if (user.UserId is not { } id)
            return ConsumerErrors.CurrentUserRequired();

        var page = await sessions.ListPageAsync(id, "consumer.me.sessions", query.Cursor, query.Limit, ct);
        if (page.IsError)
            return page.Errors;

        return new Page<ConsumerSignInSession>
        {
            Items = page.Value.Items.Select(session => new ConsumerSignInSession(
                session.Id, session.Device, session.CreatedAt, session.LastUsedAt, session.Status,
                session.Id == query.CurrentSessionId)).ToArray(),
            NextCursor = page.Value.NextCursor,
            HasNextPage = page.Value.HasNextPage
        };
    }
}
