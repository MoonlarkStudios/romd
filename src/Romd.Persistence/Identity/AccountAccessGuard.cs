using Microsoft.EntityFrameworkCore;
using Romd.Persistence;
using Romd.Domain.Identity;

namespace Romd.Persistence.Identity;

public static class AccountAccessGuard
{
    public static Task LockAsync(RomdDbContext dbContext, CancellationToken ct)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Administrative access changes require a transaction.");
        }

        // All changes that can remove an administrator share this transaction-scoped lock.
        return dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1380928836, 1)", ct);
    }

    public static Task<bool> HasOtherViableAdminAsync(RomdDbContext dbContext, Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return (from user in dbContext.Users
                join membership in dbContext.UserRoles on user.Id equals membership.UserId
                join role in dbContext.Roles on membership.RoleId equals role.Id
                where user.Id != userId && user.Id != SystemActor.UserId &&
                      !user.IsSuspended && !user.RequiresActivation && user.PasswordHash != null && role.Name == "Admin" &&
                      (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= now)
                select user.Id).AnyAsync(ct);
    }

}
