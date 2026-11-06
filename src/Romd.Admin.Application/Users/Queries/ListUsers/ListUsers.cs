using ErrorOr;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Users.Queries.ListUsers;

public sealed record ListUsersQuery : IQuery<IReadOnlyList<ManagedUser>>;

public sealed class ListUsersQueryHandler(IUserAdministration users)
    : IQueryHandler<ListUsersQuery, IReadOnlyList<ManagedUser>>
{
    public async Task<ErrorOr<IReadOnlyList<ManagedUser>>> HandleAsync(
        ListUsersQuery query,
        CancellationToken ct = default) =>
        ErrorOrFactory.From(await users.ListAsync(ct));
}
