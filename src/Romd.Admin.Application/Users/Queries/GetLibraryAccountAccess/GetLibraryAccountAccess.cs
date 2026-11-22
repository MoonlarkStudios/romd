using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetLibraryAccountAccess;

public sealed record GetLibraryAccountAccessQuery(Guid UserId, int? TitleId) : IQuery<LibraryAccountAccess>;

public interface ILibraryAccountAccessReader
{
    Task<ErrorOr<LibraryAccountAccess>> ReadAsync(Guid userId, int? titleId, CancellationToken ct);
}

public sealed class GetLibraryAccountAccessQueryHandler(ILibraryAccountAccessReader access, IReadSnapshotTransactionFactory snapshots,
    ILogger<GetLibraryAccountAccessQueryHandler> logger) : IQueryHandler<GetLibraryAccountAccessQuery, LibraryAccountAccess>
{
    public Task<ErrorOr<LibraryAccountAccess>> HandleAsync(GetLibraryAccountAccessQuery query, CancellationToken ct = default) =>
        snapshots.ExecuteReadSnapshotAsync(token => access.ReadAsync(query.UserId, query.TitleId, token), logger, ct);
}
