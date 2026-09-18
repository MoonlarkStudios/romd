using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetUserDirectory;
public sealed record GetUserDirectoryQuery(string? Search, string? Role, string? Status, string? LibraryId, string? Cursor) : IQuery<UserDirectoryPageDto>;
public interface IUserDirectoryReader
{
    Task<ErrorOr<UserDirectoryPageDto>> ReadAsync(GetUserDirectoryQuery query, CancellationToken ct);
}
public sealed class GetUserDirectoryQueryHandler(IUserDirectoryReader reader) : IQueryHandler<GetUserDirectoryQuery, UserDirectoryPageDto>
{
    public Task<ErrorOr<UserDirectoryPageDto>> HandleAsync(GetUserDirectoryQuery query, CancellationToken ct = default) => reader.ReadAsync(query, ct);
}
