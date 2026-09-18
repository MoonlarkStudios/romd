using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetAccountSecurity;
public sealed record GetAccountSecurityQuery(Guid UserId, Guid? CurrentSessionId = null) : IQuery<AccountSecurityDto>;
public sealed class GetAccountSecurityQueryHandler(IAccountLifecycle lifecycle) : IQueryHandler<GetAccountSecurityQuery, AccountSecurityDto>
{
    public async Task<ErrorOr<AccountSecurityDto>> HandleAsync(GetAccountSecurityQuery query, CancellationToken ct = default)
    {
        var result = await lifecycle.GetSecurityAsync(query.UserId, ct);
        if (result.IsError) return result.Errors;
        return result.Value with { Sessions = result.Value.Sessions.Select(session => session with
            { IsCurrent = session.Id == query.CurrentSessionId?.ToString() && session.Status == "Current" }).ToList() };
    }
}
