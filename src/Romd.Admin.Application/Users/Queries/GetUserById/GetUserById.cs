using ErrorOr;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Users.Queries.GetUserById;

public sealed record GetUserByIdQuery(Guid UserId) : IQuery<ManagedUser>;

public sealed class GetUserByIdQueryHandler(IUserAdministration users)
    : IQueryHandler<GetUserByIdQuery, ManagedUser>
{
    public async Task<ErrorOr<ManagedUser>> HandleAsync(
        GetUserByIdQuery query,
        CancellationToken ct = default) =>
        await users.GetByIdAsync(query.UserId, ct) is { } user
            ? user
            : UserErrors.NotFound();
}
