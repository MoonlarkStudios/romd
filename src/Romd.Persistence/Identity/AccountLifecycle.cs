using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using Romd.Admin.Application.Users;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Identity;

public sealed class AccountLifecycle(
    RomdDbContext db,
    UserManager<RomdUser> users,
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations,
    IAccountSessions accountSessions,
    IAuditContext actor,
    TimeProvider clock) : IAccountLifecycle
{
    public async Task<ErrorOr<AccountSecurityDto>> GetSecurityAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(user => user.Id == userId, ct)) return UserErrors.NotFound();
        var now = clock.GetUtcNow();
        var links = await db.AccountLinks.Where(link => link.UserId == userId)
            .OrderByDescending(link => link.CreatedAt).Take(20).ToListAsync(ct);
        var sessions = await accountSessions.ListAsync(userId, ct);
        return new AccountSecurityDto(links.Select(link => new AccountLinkDto(link.Id, link.Purpose,
            link.CreatedAt, link.ExpiresAt, link.RedeemedAt.HasValue ? "Accepted" : link.RevokedAt.HasValue ? "Revoked" :
                link.ExpiresAt <= now ? "Expired" : "Pending")).ToList(),
            sessions.Select(session => new AccountSessionDto(session.Id.ToString(),
                session.ClientId == "romd-admin-spa" ? "ROMD Admin" : session.ClientId == "romd-consumer-spa" ? "ROMD Consumer" : "ROMD Console",
                session.CreatedAt, session.ExpiresAt)
            {
                Device = session.Device, LastUsedAt = session.LastUsedAt, Status = session.Status,
                RevokedAt = session.RevokedAt, RevokedBy = session.RevokedBy, RevocationReason = session.RevocationReason
            }).ToList());
    }

    public async Task<ErrorOr<IssuedAccountLinkDto>> IssueLinkAsync(Guid userId, string purpose, CancellationToken ct)
    {
        if (purpose is not ("activation" or "recovery"))
            return Error.Validation("Account.InvalidPurpose", "Choose activation or recovery.");
        if (userId == SystemActor.UserId) return Protected();
        await AccountAccessGuard.LockAsync(db, ct);
        var user = await db.Users.AsTracking().SingleOrDefaultAsync(user => user.Id == userId, ct);
        if (user is null) return UserErrors.NotFound();
        if (user.IsSuspended) return Error.Conflict("Account.Suspended", "Reactivate the account before issuing a link.");
        if (purpose == "activation" && !user.RequiresActivation)
            return Error.Conflict("Account.AlreadyActivated", "This account is already activated. Use a recovery link instead.");
        if (purpose == "recovery" && user.RequiresActivation)
            return Error.Conflict("Account.PendingActivation", "Issue an activation link for this account.");
        var now = clock.GetUtcNow();
        await RevokePendingLinksAsync(userId, now, ct);
        string raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var link = new AccountLinkEntity
        {
            UserId = userId, Purpose = purpose, TokenHash = Hash(raw), CreatedAt = now,
            ExpiresAt = now.AddHours(purpose == "activation" ? 48 : 1)
        };
        db.AccountLinks.Add(link);
        Evidence(userId, purpose == "activation" ? "Activation link issued" : "Recovery link issued");
        return new IssuedAccountLinkDto(userId, raw, link.ExpiresAt);
    }

    public async Task<ErrorOr<Success>> RevokeLinkAsync(Guid userId, Guid linkId, CancellationToken ct)
    {
        if (userId == SystemActor.UserId) return Protected();
        await AccountAccessGuard.LockAsync(db, ct);
        var link = await db.AccountLinks.AsTracking().SingleOrDefaultAsync(link => link.Id == linkId && link.UserId == userId, ct);
        if (link is null) return Error.NotFound("Account.LinkNotFound", "Link not found.");
        if (link.RedeemedAt.HasValue || link.RevokedAt.HasValue) return Result.Success;
        link.RevokedAt = clock.GetUtcNow();
        Evidence(userId, "Account link revoked");
        return Result.Success;
    }

    public async Task<ErrorOr<Success>> RedeemLinkAsync(string token, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64 || string.IsNullOrWhiteSpace(password)) return InvalidLink();
        string hash = Hash(token);
        if (!await db.AccountLinks.AnyAsync(link => link.TokenHash == hash && link.RedeemedAt == null && link.RevokedAt == null, ct))
            return InvalidLink();
        await AccountAccessGuard.LockAsync(db, ct);
        var link = await db.AccountLinks.AsTracking().SingleOrDefaultAsync(link => link.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (link is null || link.RedeemedAt.HasValue || link.RevokedAt.HasValue || link.ExpiresAt <= now || link.UserId == SystemActor.UserId)
            return InvalidLink();
        var user = await db.Users.AsTracking().SingleOrDefaultAsync(user => user.Id == link.UserId, ct);
        if (user is null || user.IsSuspended) return InvalidLink();
        string resetToken = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, resetToken, password);
        if (!reset.Succeeded) return Error.Validation("Account.PasswordPolicy", string.Join(" ", reset.Errors.Select(error => error.Description)));
        user.RequiresActivation = false;
        user.EmailConfirmed = true;
        user.UpdatedAt = now;
        link.RedeemedAt = now;
        await RevokePendingLinksAsync(user.Id, now, ct, link.Id);
        await RevokeAllTokensAsync(user.Id, ct);
        Evidence(user.Id, link.Purpose == "activation" ? "Account activated" : "Password recovered", user.Id);
        return Result.Success;
    }

    public async Task<ErrorOr<Success>> SetSuspensionAsync(Guid userId, bool suspended, CancellationToken ct)
    {
        if (userId == SystemActor.UserId) return Protected();
        if (suspended && userId == actor.ActorId) return Error.Conflict("Account.SelfSuspension", "You cannot suspend your own account.");
        await AccountAccessGuard.LockAsync(db, ct);
        var user = await db.Users.AsTracking().SingleOrDefaultAsync(user => user.Id == userId, ct);
        if (user is null) return UserErrors.NotFound();
        if (suspended && await users.IsInRoleAsync(user, "Admin") && !await AccountAccessGuard.HasOtherViableAdminAsync(db, userId, ct))
            return Error.Conflict("Account.LastAdmin", "Keep at least one active administrator.");
        user.IsSuspended = suspended;
        user.UpdatedAt = clock.GetUtcNow();
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        if (suspended)
        {
            await RevokeAllTokensAsync(userId, ct);
            await RevokePendingLinksAsync(userId, clock.GetUtcNow(), ct);
        }
        Evidence(userId, suspended ? "Account suspended" : "Account reactivated");
        return Result.Success;
    }

    public async Task<ErrorOr<Success>> RevokeSessionsAsync(Guid userId, string? sessionId, CancellationToken ct, Guid? exceptSessionId = null)
    {
        if (userId == SystemActor.UserId) return Protected();
        await AccountAccessGuard.LockAsync(db, ct);
        if (!await db.Users.AnyAsync(user => user.Id == userId, ct)) return UserErrors.NotFound();
        Guid? id = null;
        if (sessionId is not null)
        {
            if (!Guid.TryParse(sessionId, out var parsed)) return Error.Validation("Account.InvalidSession", "Invalid session.");
            id = parsed;
        }
        if (exceptSessionId.HasValue)
        {
            var ownSessions = await accountSessions.ListAsync(userId, ct);
            if (!ownSessions.Any(session => session.Id == exceptSessionId && session.Status == "Current"))
                return Error.Conflict("Account.CurrentSessionRequired", "Sign in again before revoking other sessions.");
        }
        if (!await accountSessions.RevokeAsync(userId, id, exceptSessionId, actor.ActorId,
            exceptSessionId.HasValue ? "Other sessions revoked" : "Revoked by account administrator", ct)) return UserErrors.NotFound();
        if (sessionId is null && !exceptSessionId.HasValue)
        {
            await RevokeAllTokensAsync(userId, ct);
            await db.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(setters =>
                setters.SetProperty(user => user.SecurityStamp, Guid.NewGuid().ToString()), ct);
        }
        Evidence(userId, exceptSessionId.HasValue ? "Other sessions revoked" : sessionId is null ? "All sessions revoked" : "Session revoked");
        return Result.Success;
    }

    private async Task RevokeAllTokensAsync(Guid userId, CancellationToken ct)
    {
        await accountSessions.RevokeAsync(userId, null, null, actor.ActorId, "Account security changed", ct);
        await tokens.RevokeBySubjectAsync(userId.ToString(), ct);
        await authorizations.RevokeBySubjectAsync(userId.ToString(), ct);
    }

    private Task RevokePendingLinksAsync(Guid userId, DateTimeOffset now, CancellationToken ct, Guid? except = null) =>
        db.AccountLinks.Where(link => link.UserId == userId && link.Id != except && link.RedeemedAt == null && link.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.RevokedAt, now), ct);

    private void Evidence(Guid userId, string action, Guid? actorId = null) => db.AdminAuditEvents.Add(new()
    {
        ActorId = actorId ?? actor.ActorId, OccurredAt = clock.GetUtcNow(), TargetType = "User",
        TargetId = userId.ToString(), Action = action
    });
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    private static Error InvalidLink() => Error.Validation("Account.InvalidLink", "This link is invalid, expired, or already used. Ask an administrator for a new link.");
    private static Error Protected() => UserErrors.ProtectedUser("The ROMD system account cannot be managed as a human account.");
}
