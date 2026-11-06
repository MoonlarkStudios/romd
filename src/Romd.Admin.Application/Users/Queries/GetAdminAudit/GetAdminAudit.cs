using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetAdminAudit;

public interface IAdminAuditReader
{
    Task<ErrorOr<Page<AdminAuditEventDto>>> ReadAsync(string? cursor, string? targetType, string? targetId, int limit, CancellationToken ct);
}
public sealed record GetAdminAuditQuery(string? Cursor, string? TargetType, string? TargetId, int Limit = 50) : IQuery<Page<AdminAuditEventDto>>;
public sealed class GetAdminAuditQueryHandler(IAdminAuditReader reader) : IQueryHandler<GetAdminAuditQuery, Page<AdminAuditEventDto>>
{
    public Task<ErrorOr<Page<AdminAuditEventDto>>> HandleAsync(GetAdminAuditQuery query, CancellationToken ct = default) =>
        reader.ReadAsync(query.Cursor, query.TargetType, query.TargetId, query.Limit, ct);
}
