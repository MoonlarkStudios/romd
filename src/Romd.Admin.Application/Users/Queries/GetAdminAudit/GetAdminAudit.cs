using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Queries.GetAdminAudit;

public interface IAdminAuditReader
{
    Task<AdminAuditPageDto> ReadAsync(long? before, string? targetType, string? targetId, CancellationToken ct);
}
public sealed record GetAdminAuditQuery(string? Before, string? TargetType, string? TargetId) : IQuery<AdminAuditPageDto>;
public sealed class GetAdminAuditQueryHandler(IAdminAuditReader reader) : IQueryHandler<GetAdminAuditQuery, AdminAuditPageDto>
{
    public async Task<ErrorOr<AdminAuditPageDto>> HandleAsync(GetAdminAuditQuery query, CancellationToken ct = default)
    {
        long? before = null;
        if (query.Before is not null)
        {
            if (!long.TryParse(query.Before, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
                return Error.Validation("Audit.InvalidCursor", "The audit cursor is invalid.");
            before = value;
        }
        return await reader.ReadAsync(before, query.TargetType, query.TargetId, ct);
    }
}
