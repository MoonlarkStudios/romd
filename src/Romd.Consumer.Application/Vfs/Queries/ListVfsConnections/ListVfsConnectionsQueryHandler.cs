using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Common.Models;

namespace Romd.Consumer.Application.Vfs.Queries.ListVfsConnections;

public sealed class ListVfsConnectionsQueryHandler : IQueryHandler<ListVfsConnectionsQuery, Page<VfsConnection>>
{
    private readonly ICurrentUser currentUser;
    private readonly IVfsConnections connections;

    public ListVfsConnectionsQueryHandler(ICurrentUser currentUser, IVfsConnections connections)
    {
        this.currentUser = currentUser;
        this.connections = connections;
    }

    public async Task<ErrorOr<Page<VfsConnection>>> HandleAsync(ListVfsConnectionsQuery query, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        return await connections.ListPageAsync(userId, query.Cursor, query.Limit, ct);
    }
}
