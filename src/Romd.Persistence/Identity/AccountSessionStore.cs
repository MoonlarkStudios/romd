using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Security;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Identity;

public sealed class AccountSessionStore(RomdDbContext db, TimeProvider clock) : IAccountSessions, IAccountSessionCreationStore
{
    public Task<AccountSession> StageStartAsync(AccountSessionCreationFacts facts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Starting an account session requires a caller-owned transaction.");
        var entity = new AccountSessionEntity
        {
            Id = facts.Id,
            UserId = facts.UserId,
            ClientId = facts.ClientId,
            Device = facts.Device,
            AccountStamp = facts.AccountStamp,
            CreatedAt = facts.CreatedAt,
            LastUsedAt = facts.CreatedAt,
            ExpiresAt = facts.ExpiresAt
        };
        db.AccountSessions.Add(entity);
        return Task.FromResult(ToModel(entity, "Current"));
    }

    public async Task<AccountSession?> ValidateAsync(Guid userId, Guid sessionId, string accountStamp, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await (from item in db.AccountSessions.AsNoTracking()
                             join user in db.Users on item.UserId equals user.Id
                             where item.Id == sessionId && item.UserId == userId && item.RevokedAt == null && item.ExpiresAt > now
                                 && item.AccountStamp == accountStamp && user.SecurityStamp == accountStamp
                                 && !user.IsSuspended && !user.RequiresActivation
                             select item).SingleOrDefaultAsync(ct);
        if (session is null)
            return null;
        // Conditional update bounds activity writes to once per five minutes, even across hosts.
        if (session.LastUsedAt <= now.AddMinutes(-5))
            await db.AccountSessions.Where(item => item.Id == sessionId && item.RevokedAt == null
                && item.LastUsedAt <= now.AddMinutes(-5)).ExecuteUpdateAsync(set => set.SetProperty(item => item.LastUsedAt, now), ct);
        return ToModel(session, "Current");
    }

    public async Task<IReadOnlyList<AccountSession>> ListAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, ct);
        var now = clock.GetUtcNow();
        var query = db.AccountSessions.AsNoTracking().Where(item => item.UserId == userId);
        var accountEnabled = user is not null && !user.IsSuspended && !user.RequiresActivation;
        var stamp = user?.SecurityStamp;
        var current = query.Where(item => accountEnabled && item.RevokedAt == null
            && item.ExpiresAt > now && item.AccountStamp == stamp);
        var history = query.Where(item => !accountEnabled || item.RevokedAt != null
            || item.ExpiresAt <= now || item.AccountStamp != stamp);
        var sessions = await current.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToListAsync(ct);
        sessions.AddRange(await history.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(200).ToListAsync(ct));
        return sessions.Select(item => ToModel(item, item.RevokedAt.HasValue ? "Revoked" :
            user is null || user.IsSuspended || user.RequiresActivation || item.AccountStamp != user.SecurityStamp ? "Invalidated" :
            item.ExpiresAt <= now ? "Expired" : "Current")).ToList();
    }

    public async Task<ErrorOr<PagedList<AccountSession>>> ListPageAsync(Guid userId, string scope, string? cursor, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            return FieldErrors.Validation("Account.InvalidLimit", "Use a limit between 1 and 100.", "limit");
        string fingerprint = KeysetCursor.Fingerprint(scope, userId, "createdAt,id:desc");
        var decoded = KeysetCursor.Decode<DateIdPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return FieldErrors.Validation("Account.InvalidCursor", "The cursor is invalid for this query.", "cursor");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, ct);
        var now = clock.GetUtcNow();
        var query = db.AccountSessions.AsNoTracking().Where(item => item.UserId == userId);
        if (decoded.Value is { } position)
            query = query.Where(item => item.CreatedAt < position.CreatedAt || item.CreatedAt == position.CreatedAt && item.Id.CompareTo(position.Id) < 0);
        var rows = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(limit + 1).ToListAsync(ct);
        return new PagedList<AccountSession>(rows.Take(limit).Select(item => ToModel(item, item.RevokedAt.HasValue ? "Revoked" : user is null || user.IsSuspended || user.RequiresActivation || item.AccountStamp != user.SecurityStamp ? "Invalidated" : item.ExpiresAt <= now ? "Expired" : "Current")).ToArray(), rows.Count > limit ? KeysetCursor.Encode(new DateIdPosition(rows[limit - 1].CreatedAt, rows[limit - 1].Id), fingerprint) : null, rows.Count > limit);
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid? sessionId, Guid? exceptSessionId, Guid actorId, string reason, CancellationToken ct)
    {
        var query = db.AccountSessions.Where(item => item.UserId == userId);
        if (sessionId.HasValue)
            query = query.Where(item => item.Id == sessionId.Value);
        if (exceptSessionId.HasValue)
            query = query.Where(item => item.Id != exceptSessionId.Value);
        if (sessionId.HasValue && !await query.AnyAsync(ct))
            return false;
        await query.Where(item => item.RevokedAt == null).ExecuteUpdateAsync(set => set
            .SetProperty(item => item.RevokedAt, clock.GetUtcNow()).SetProperty(item => item.RevokedBy, actorId)
            .SetProperty(item => item.RevocationReason, reason), ct);
        return true;
    }

    public Task SetDeviceAsync(Guid userId, Guid sessionId, string device, CancellationToken ct) =>
        db.AccountSessions.Where(item => item.UserId == userId && item.Id == sessionId && item.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Device, AccountSessionDeviceLabel.Clean(device)), ct);

    private static AccountSession ToModel(AccountSessionEntity item, string status) => new(item.Id, item.UserId,
        item.ClientId, item.Device, item.CreatedAt, item.LastUsedAt, item.ExpiresAt, item.RevokedAt, item.RevokedBy, item.RevocationReason, status);
}
