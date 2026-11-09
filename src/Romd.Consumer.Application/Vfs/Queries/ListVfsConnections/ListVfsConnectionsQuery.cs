using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Common.Models;

namespace Romd.Consumer.Application.Vfs.Queries.ListVfsConnections;

public sealed record ListVfsConnectionsQuery(string? Cursor = null, int Limit = 50) : IQuery<Page<VfsConnection>>
{
    public const int MaxLimit = 100;
}
