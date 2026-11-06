using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users.Queries.GetAdminAudit;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Users;

namespace Romd.Persistence.Queries;

public sealed class AdminAuditReader(RomdDbContext db) : IAdminAuditReader
{
    public async Task<ErrorOr<Page<AdminAuditEventDto>>> ReadAsync(string? cursor, string? targetType, string? targetId, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            return FieldErrors.Validation("Audit.InvalidLimit", "Use a limit between 1 and 100.", "limit");
        string fingerprint = KeysetCursor.Fingerprint("admin.audit", targetType, targetId, "id:desc");
        var decoded = KeysetCursor.Decode<AuditPosition>(cursor, fingerprint, p => p.Id > 0);
        if (decoded.IsError)
            return FieldErrors.Validation("Audit.InvalidCursor", "The cursor is invalid for this query.", "cursor");
        var query = db.AdminAuditEvents.AsNoTracking();
        if (decoded.Value is { } position)
            query = query.Where(item => item.Id < position.Id);
        if (targetType is not null)
            query = query.Where(item => item.TargetType == targetType);
        if (targetId is not null)
            query = query.Where(item => item.TargetId == targetId);
        var rows = await query.OrderByDescending(item => item.Id).Take(limit + 1).ToListAsync(ct);
        var actorIds = rows.Take(limit).Select(item => item.ActorId).Distinct().ToArray();
        var actors = await db.Users.Where(user => actorIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.Email, ct);
        return new Page<AdminAuditEventDto>
        {
            Items = rows.Take(limit).Select(item => new AdminAuditEventDto(LongIdCoder.Encode(item.Id), item.ActorId, actors.GetValueOrDefault(item.ActorId), item.OccurredAt, item.Action, item.TargetType, item.TargetId, item.Changes)).ToArray(),
            HasNextPage = rows.Count > limit,
            NextCursor = rows.Count > limit ? KeysetCursor.Encode(new AuditPosition(rows[limit - 1].Id), fingerprint) : null
        };
    }
    private sealed record AuditPosition(long Id);
}
