using Romd.Contracts.Common.Models;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetUserDirectory;

public sealed record GetUserDirectoryQuery(string? Search, string? Role, string? Status, string? LibraryId, string? Cursor, int Limit = 50) : IQuery<Page<User>>;
public interface IUserDirectoryReader
{
    Task<ErrorOr<Page<User>>> ReadAsync(GetUserDirectoryQuery query, CancellationToken ct);
}
public sealed class GetUserDirectoryQueryHandler(IUserDirectoryReader reader) : IQueryHandler<GetUserDirectoryQuery, Page<User>>
{
    public Task<ErrorOr<Page<User>>> HandleAsync(GetUserDirectoryQuery query, CancellationToken ct = default) => reader.ReadAsync(query, ct);
}
