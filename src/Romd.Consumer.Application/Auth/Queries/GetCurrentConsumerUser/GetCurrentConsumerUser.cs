using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Auth;

namespace Romd.Consumer.Application.Auth.Queries.GetCurrentConsumerUser;

public sealed record GetCurrentConsumerUserQuery : IQuery<CurrentUser>;

public sealed class GetCurrentConsumerUserQueryHandler(ICurrentUser currentUser)
    : IQueryHandler<GetCurrentConsumerUserQuery, CurrentUser>
{
    public Task<ErrorOr<CurrentUser>> HandleAsync(
        GetCurrentConsumerUserQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return Task.FromResult<ErrorOr<CurrentUser>>(ConsumerErrors.CurrentUserRequired());
        }

        IReadOnlyList<string> roles = currentUser.Roles is { Count: > 0 } currentRoles
            ? currentRoles
            : [currentUser.Role.ToString()];

        return Task.FromResult<ErrorOr<CurrentUser>>(new CurrentUser(
            userId,
            currentUser.Email ?? string.Empty,
            currentUser.UserName ?? string.Empty,
            roles));
    }
}
