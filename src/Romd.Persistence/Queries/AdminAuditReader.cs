using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users.Queries.GetAdminAudit;
using Romd.Contracts.Management.Users;

namespace Romd.Persistence.Queries;
public sealed class AdminAuditReader(RomdDbContext db) : IAdminAuditReader
{
    public async Task<AdminAuditPageDto> ReadAsync(long? before, string? targetType, string? targetId, CancellationToken ct)
    {
        var query = db.AdminAuditEvents.AsNoTracking();
        if (before.HasValue) query = query.Where(item => item.Id < before.Value);
        if (!string.IsNullOrEmpty(targetType)) query = query.Where(item => item.TargetType == targetType);
        if (!string.IsNullOrEmpty(targetId)) query = query.Where(item => item.TargetId == targetId);
        var rows = await (from item in query
            join user in db.Users on item.ActorId equals user.Id into actors
            from user in actors.DefaultIfEmpty()
            orderby item.Id descending
            select new AdminAuditEventDto(item.Id.ToString(), item.ActorId, user == null ? null : user.Email,
                item.OccurredAt, item.Action, item.TargetType, item.TargetId, item.Changes))
            .Take(51).ToListAsync(ct);
        return new(rows.Take(50).ToList(), rows.Count > 50 ? rows[49].Id : null);
    }
}
