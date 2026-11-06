using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;

namespace Romd.Persistence.Identity;

/// <summary>Operator-only recovery; registered only by the worker's local command.</summary>
public sealed class LocalAdminRecovery(
    RomdDbContext db,
    ILookupNormalizer normalizer,
    IAccountLifecycle lifecycle,
    IAuditContext actor,
    TimeProvider clock)
{
    public Task<string> GetRuntimeRoleAsync(CancellationToken ct) =>
        db.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync(ct);

    public async Task<ErrorOr<IssuedAccountLinkDto>> IssueAsync(string email, CancellationToken ct)
    {
        if (!actor.IsSystem || actor.ActorId != SystemActor.UserId)
            return Error.Forbidden("Account.LocalRecoveryOnly", "Administrator recovery requires the local system actor.");

        await AccountAccessGuard.LockAsync(db, ct);
        string normalizedEmail = normalizer.NormalizeEmail(email);
        var userId = await (from user in db.Users
                            join membership in db.UserRoles on user.Id equals membership.UserId
                            join role in db.Roles on membership.RoleId equals role.Id
                            where user.NormalizedEmail == normalizedEmail && user.Id != SystemActor.UserId && role.Name == "Admin"
                            select (Guid?)user.Id).SingleOrDefaultAsync(ct);
        if (userId is null)
            return Error.NotFound("Account.AdminNotFound", "No existing human administrator matches that email.");

        // These operations share the caller's transaction and the administrative access lock.
        // Existing suspension/activation rules still apply; recovery never grants roles.
        var issued = await lifecycle.IssueLinkAsync(userId.Value, "recovery", ct);
        if (issued.IsError)
            return issued.Errors;
        var revoked = await lifecycle.RevokeSessionsAsync(userId.Value, null, ct);
        if (revoked.IsError)
            return revoked.Errors;

        db.AdminAuditEvents.Add(new()
        {
            ActorId = SystemActor.UserId,
            OccurredAt = clock.GetUtcNow(),
            TargetType = "User",
            TargetId = userId.Value.ToString(),
            Action = "Local administrator recovery initiated"
        });
        return issued.Value;
    }
}
