using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetAccountSessions;

public sealed record GetAccountSessionsQuery(Guid UserId, string? Cursor = null, int Limit = 50,
    Guid? CurrentSessionId = null, string Scope = "admin.users.sessions") : IQuery<Page<AccountSession>>;
public sealed class GetAccountSessionsQueryHandler(IAccountLifecycle lifecycle) : IQueryHandler<GetAccountSessionsQuery, Page<AccountSession>>
{
    public Task<ErrorOr<Page<AccountSession>>> HandleAsync(GetAccountSessionsQuery query, CancellationToken ct = default) =>
        lifecycle.GetSessionsAsync(query.UserId, query.Cursor, query.Limit, query.CurrentSessionId, query.Scope, ct);
}
