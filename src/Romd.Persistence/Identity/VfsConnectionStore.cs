using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Romd.Domain.Vfs;
using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;
using Romd.Persistence.Entities;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Common.Models;

namespace Romd.Persistence.Identity;

public sealed class VfsConnectionStore : IVfsConnections
{
    private readonly RomdDbContext db;
    private readonly TimeProvider clock;

    public VfsConnectionStore(RomdDbContext db, TimeProvider clock)
    {
        this.db = db;
        this.clock = clock;
    }

    public async Task<IReadOnlyList<VfsConnectionDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var activeIds = await ActiveConnections().Where(item => item.UserId == userId)
            .Select(item => item.Id).ToArrayAsync(ct);
        // Active connections always precede the bounded history.
        var entries = await db.VfsConnections.Where(item => item.UserId == userId)
            .OrderBy(item => !activeIds.Contains(item.Id))
            .ThenByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToListAsync(ct);
        return entries.Select(item => Describe(item, activeIds.Contains(item.Id))).ToArray();
    }

    public async Task<ErrorOr<Page<VfsConnectionDto>>> ListPageAsync(Guid userId, string? cursor, int limit, CancellationToken ct)
    {
        string fingerprint = KeysetCursor.Fingerprint("consumer.connections", userId);
        var decoded = KeysetCursor.Decode<ConnectionPosition>(cursor, fingerprint,
            value => value.CreatedAt != default && value.Id != Guid.Empty);
        if (decoded.IsError)
            return decoded.Errors;
        var active = ActiveConnections().Where(item => item.UserId == userId);
        var query = db.VfsConnections.Where(item => item.UserId == userId)
            .Select(item => new { Item = item, Active = active.Any(connection => connection.Id == item.Id) });
        if (decoded.Value is { } position)
            query = query.Where(row => position.Active && !row.Active
                || row.Active == position.Active && (row.Item.CreatedAt < position.CreatedAt
                    || row.Item.CreatedAt == position.CreatedAt && row.Item.Id.CompareTo(position.Id) < 0));
        var rows = await query.OrderByDescending(row => row.Active).ThenByDescending(row => row.Item.CreatedAt)
            .ThenByDescending(row => row.Item.Id).Take(limit + 1).ToListAsync(ct);
        bool more = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        return new Page<VfsConnectionDto>
        {
            Items = rows.Select(row => Describe(row.Item, row.Active)).ToArray(),
            HasNextPage = more,
            NextCursor = more ? KeysetCursor.Encode(new ConnectionPosition(rows[^1].Active, rows[^1].Item.CreatedAt, rows[^1].Item.Id), fingerprint) : null
        };
    }

    private sealed record ConnectionPosition(bool Active, DateTimeOffset CreatedAt, Guid Id);

    public async Task<ErrorOr<IssuedVfsCredential>> CreateAsync(Guid userId, string name, CancellationToken ct, VfsFormat format = VfsFormat.Stored)
    {
        RequireTransaction();
        if (!VfsFormats.IsValid(format))
            return VfsConnectionErrors.InvalidFormat();
        if (!VfsConnectionErrors.IsValidName(name))
            return VfsConnectionErrors.InvalidName();
        await LockUserAsync(userId, ct);
        var user = await EnabledUserAsync(userId, ct);
        if (user is null)
            return ConsumerErrors.CurrentUserRequired();
        if (await AtLimitAsync(user, ct))
            return VfsConnectionErrors.LimitReached();
        var now = clock.GetUtcNow();
        var item = new VfsConnectionEntity { UserId = userId, Name = name.Trim(), Format = VfsFormats.ToWire(format), CreatedAt = now };
        string password = Issue(item, user, now);
        db.VfsConnections.Add(item);
        return new IssuedVfsCredential(Describe(item, true), password);
    }

    public async Task<ErrorOr<VfsConnectionDto>> RenameAsync(Guid userId, Guid id, string? name, CancellationToken ct, VfsFormat? format = null)
    {
        if (name is not null && !VfsConnectionErrors.IsValidName(name))
            return VfsConnectionErrors.InvalidName();
        if (format is { } requested && !VfsFormats.IsValid(requested))
            return VfsConnectionErrors.InvalidFormat();
        string? wire = format is { } selection ? VfsFormats.ToWire(selection) : null;
        int changed = await db.VfsConnections.Where(item => item.Id == id && item.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Name, item => name == null ? item.Name : name.Trim())
                .SetProperty(item => item.Format, item => wire ?? item.Format), ct);
        if (changed == 0)
            return VfsConnectionErrors.NotFound();
        var item = await db.VfsConnections.SingleAsync(item => item.Id == id, ct);
        bool active = await ActiveConnections().AnyAsync(connection => connection.Id == id, ct);
        return Describe(item, active);
    }

    public async Task<ErrorOr<IssuedVfsCredential>> RotateAsync(Guid userId, Guid id, CancellationToken ct)
    {
        RequireTransaction();
        await LockUserAsync(userId, ct);
        var user = await EnabledUserAsync(userId, ct);
        if (user is null)
            return ConsumerErrors.CurrentUserRequired();
        var item = await db.VfsConnections.AsTracking().SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, ct);
        if (item is null)
            return VfsConnectionErrors.NotFound();
        // An earlier create can still be tracked after an atomic setting update in this scope.
        // Rotation must describe the current row, including its format, before issuing a secret.
        await db.Entry(item).ReloadAsync(ct);
        if (db.Entry(item).State == EntityState.Detached || item.UserId != userId)
            return VfsConnectionErrors.NotFound();
        if (item.RevokedAt is not null)
            return VfsConnectionErrors.Revoked();
        if (item.AccountStamp != user.SecurityStamp && await AtLimitAsync(user, ct))
            return VfsConnectionErrors.LimitReached();
        string password = Issue(item, user, clock.GetUtcNow());
        return new IssuedVfsCredential(Describe(item, true), password);
    }

    public async Task<ErrorOr<Success>> RevokeAsync(Guid userId, Guid id, CancellationToken ct)
    {
        RequireTransaction();
        await LockUserAsync(userId, ct);
        var now = clock.GetUtcNow();
        int changed = await db.VfsConnections.Where(item => item.Id == id && item.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RevokedAt, item => item.RevokedAt ?? now), ct);
        return changed == 0 ? VfsConnectionErrors.NotFound() : Result.Success;
    }

    public async Task<AuthenticatedVfsConnection?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        if (username.Length != 37 || !username.StartsWith("romd-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(username.AsSpan(5), "N", out var id) || password.Length != 43)
            return null;
        var match = await ActiveConnections().SingleOrDefaultAsync(item => item.Id == id, ct);
        byte[] candidate = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        if (!CryptographicOperations.FixedTimeEquals(candidate, match?.PasswordVerifier ?? new byte[32]) || match is null)
            return null;
        var format = VfsFormats.Parse(match.Format);
        if (format.IsError)
            return null;
        var now = clock.GetUtcNow();
        var before = now.AddMinutes(-5);
        await db.VfsConnections.Where(item => item.Id == id && (item.LastUsedAt == null || item.LastUsedAt < before))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastUsedAt, now), ct);
        return new(match.UserId, match.Id, format.Value);
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("VFS credential mutations require a caller-owned transaction.");
        }
    }

    // Serialize lifecycle changes and the active-connection limit across all host instances.
    private Task LockUserAsync(Guid userId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"vfs:" + userId}, 0))", ct);

    private Task<RomdUser?> EnabledUserAsync(Guid userId, CancellationToken ct) =>
        db.Users.SingleOrDefaultAsync(user => user.Id == userId && !user.IsSuspended && !user.RequiresActivation && user.SecurityStamp != null, ct);

    private async Task<bool> AtLimitAsync(RomdUser user, CancellationToken ct) =>
        await ActiveConnections().CountAsync(item => item.UserId == user.Id, ct) >= 32;

    private IQueryable<VfsConnectionEntity> ActiveConnections() =>
        from item in db.VfsConnections
        join user in db.Users on item.UserId equals user.Id
        where item.RevokedAt == null && item.AccountStamp == user.SecurityStamp &&
              !user.IsSuspended && !user.RequiresActivation
        select item;

    private static string Issue(VfsConnectionEntity item, RomdUser user, DateTimeOffset now)
    {
        // 256 random bits: a fast one-way verifier is appropriate, unlike a human password KDF.
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        item.PasswordVerifier = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        item.AccountStamp = user.SecurityStamp!;
        item.CredentialUpdatedAt = now;
        item.LastUsedAt = null;
        return password;
    }

    private static VfsConnectionDto Describe(VfsConnectionEntity item, bool active) => new(
        item.Id, item.Name, $"romd-{item.Id:N}", item.RevokedAt is not null ? "Revoked" :
            active ? "Active" : "Invalidated",
        item.CreatedAt, item.CredentialUpdatedAt, item.LastUsedAt, item.Format);
}
