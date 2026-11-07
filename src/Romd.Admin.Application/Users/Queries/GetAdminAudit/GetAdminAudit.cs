using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetAdminAudit;

public interface IAdminAuditReader
{
    Task<ErrorOr<Page<AdminAuditEvent>>> ReadAsync(string? cursor, string? targetType, string? targetId, int limit, CancellationToken ct);
}
public sealed record GetAdminAuditQuery(string? Cursor, string? TargetType, string? TargetId, int Limit = 50) : IQuery<Page<AdminAuditEvent>>;
public sealed class GetAdminAuditQueryHandler(IAdminAuditReader reader) : IQueryHandler<GetAdminAuditQuery, Page<AdminAuditEvent>>
{
    public Task<ErrorOr<Page<AdminAuditEvent>>> HandleAsync(GetAdminAuditQuery query, CancellationToken ct = default) =>
        reader.ReadAsync(query.Cursor, query.TargetType, query.TargetId, query.Limit, ct);
}
