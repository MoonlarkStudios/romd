using ErrorOr;
using System.Security.Cryptography;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Users;
using Romd.Domain.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;

namespace Romd.Infrastructure.Identity;

public sealed class UserAdministration(
    UserManager<RomdUser> userManager,
    RomdDbContext dbContext,
    Romd.Application.Common.Security.IAccountSessions accountSessions,
    Romd.Application.Common.Security.IAuditContext audit,
    IOpenIddictTokenManager tokens) : IUserAdministration
{
    public async Task<IReadOnlyList<ManagedUser>> ListAsync(CancellationToken ct = default)
    {
        var rows = await QueryRows().ToListAsync(ct);
        return Materialize(rows);
    }

    public async Task<ManagedUser?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await QueryRows(userId).ToListAsync(ct);
        return Materialize(rows).SingleOrDefault();
    }

    public async Task<ManagedUser?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var user = await userManager.FindByEmailAsync(email);
        ct.ThrowIfCancellationRequested();
        return user is null ? null : await ToManagedUserAsync(user, ct);
    }

    public Task<ErrorOr<ManagedUser>> CreatePendingAsync(string email, int? libraryId,
        DateTimeOffset createdAt, CancellationToken ct = default) =>
        CreateCoreAsync(email, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) + "aA1!", libraryId, createdAt, true, ct);

    public Task<ErrorOr<ManagedUser>> CreateAsync(string email, string password, int? libraryId,
        DateTimeOffset createdAt, CancellationToken ct = default) =>
        CreateCoreAsync(email, password, libraryId, createdAt, false, ct);

    private async Task<ErrorOr<ManagedUser>> CreateCoreAsync(string email, string password, int? libraryId,
        DateTimeOffset createdAt, bool requiresActivation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var user = new RomdUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = !requiresActivation,
            RequiresActivation = requiresActivation,
            LibraryId = libraryId,
            CreatedAt = createdAt
        };

        var result = await userManager.CreateAsync(user, password);
        ct.ThrowIfCancellationRequested();
        return result.Succeeded
            ? await ToManagedUserAsync(user, ct)
            : UserErrors.CreateFailed();
    }

    public async Task<ErrorOr<ManagedUser>> AddRoleAsync(
        Guid userId,
        RomdRoleType role,
        CancellationToken ct = default)
    {
        if (userId == SystemActor.UserId)
        {
            return UserErrors.ProtectedUser("The ROMD system account cannot be edited.");
        }

        var user = await FindUserAsync(userId, ct);
        if (user is null)
        {
            return UserErrors.NotFound();
        }

        var result = await userManager.AddToRoleAsync(user, role.ToString());
        ct.ThrowIfCancellationRequested();
        return result.Succeeded
            ? await ToManagedUserAsync(user, ct)
            : UserErrors.AssignRoleFailed();
    }

    public async Task<ErrorOr<ManagedUser>> UpdateAsync(
        Guid userId,
        string? email,
        string? newPassword,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        if (userId == SystemActor.UserId)
        {
            return UserErrors.ProtectedUser("The ROMD system account cannot be edited.");
        }

        var user = await FindUserAsync(userId, ct);
        if (user is null)
        {
            return UserErrors.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(email) && email != user.Email)
        {
            var emailResult = await userManager.SetEmailAsync(user, email);
            ct.ThrowIfCancellationRequested();
            if (!emailResult.Succeeded)
            {
                return UserErrors.UpdateEmailFailed();
            }

            var userNameResult = await userManager.SetUserNameAsync(user, email);
            ct.ThrowIfCancellationRequested();
            if (!userNameResult.Succeeded)
            {
                return UserErrors.UpdateEmailFailed();
            }
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            string token = await userManager.GeneratePasswordResetTokenAsync(user);
            ct.ThrowIfCancellationRequested();
            var passwordResult = await userManager.ResetPasswordAsync(user, token, newPassword);
            ct.ThrowIfCancellationRequested();
            if (!passwordResult.Succeeded)
            {
                return UserErrors.UpdatePasswordFailed();
            }
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            await accountSessions.RevokeAsync(userId, null, null, audit.ActorId, "Password changed", ct);
            await tokens.RevokeBySubjectAsync(userId.ToString(), ct);
        }
        user.UpdatedAt = updatedAt;
        var updateResult = await userManager.UpdateAsync(user);
        ct.ThrowIfCancellationRequested();
        return updateResult.Succeeded
            ? await ToManagedUserAsync(user, ct)
            : UserErrors.UpdateFailed();
    }

    public async Task<ErrorOr<ManagedUser>> ReplaceRoleAsync(
        Guid userId,
        RomdRoleType role,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {

        await AccountAccessGuard.LockAsync(dbContext, ct);
        if (userId == SystemActor.UserId)
        {
            return UserErrors.ProtectedUser("The ROMD system account cannot be edited.");
        }

        var user = await FindUserAsync(userId, ct);
        if (user is null)
        {
            return UserErrors.NotFound();
        }

        var roles = await userManager.GetRolesAsync(user);
        ct.ThrowIfCancellationRequested();
        if (role != RomdRoleType.Admin && roles.Contains(nameof(RomdRoleType.Admin)) &&
            !await AccountAccessGuard.HasOtherViableAdminAsync(dbContext, userId, ct))
        {
            return UserErrors.ProtectedUser("Keep at least one active administrator before changing this role.");
        }

        if (roles.Count > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(user, roles);
            ct.ThrowIfCancellationRequested();
            if (!removeResult.Succeeded)
            {
                return UserErrors.AssignRoleFailed();
            }
        }

        var addResult = await userManager.AddToRoleAsync(user, role.ToString());
        ct.ThrowIfCancellationRequested();
        if (!addResult.Succeeded)
        {
            return UserErrors.AssignRoleFailed();
        }

        await accountSessions.RevokeAsync(userId, null, null, audit.ActorId, "Account access changed", ct);
        await tokens.RevokeBySubjectAsync(userId.ToString(), ct);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.UpdatedAt = updatedAt;
        var updateResult = await userManager.UpdateAsync(user);
        ct.ThrowIfCancellationRequested();
        return updateResult.Succeeded
            ? await ToManagedUserAsync(user, ct)
            : UserErrors.UpdateFailed();
    }

    public async Task<ErrorOr<ManagedUser>> AssignLibraryAsync(
        Guid userId,
        int? libraryId,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        if (userId == SystemActor.UserId)
        {
            return UserErrors.ProtectedUser("The ROMD system account cannot be edited.");
        }

        var user = await FindUserAsync(userId, ct);
        if (user is null)
        {
            return UserErrors.NotFound();
        }

        await accountSessions.RevokeAsync(userId, null, null, audit.ActorId, "Account access changed", ct);
        await tokens.RevokeBySubjectAsync(userId.ToString(), ct);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.LibraryId = libraryId;
        user.UpdatedAt = updatedAt;
        var result = await userManager.UpdateAsync(user);
        ct.ThrowIfCancellationRequested();
        return result.Succeeded
            ? await ToManagedUserAsync(user, ct)
            : UserErrors.AssignLibraryFailed();
    }

    public async Task<ErrorOr<Deleted>> DeleteAsync(Guid userId, CancellationToken ct = default)
    {

        await AccountAccessGuard.LockAsync(dbContext, ct);
        if (userId == SystemActor.UserId)
        {
            return UserErrors.ProtectedUser("The ROMD system account cannot be edited.");
        }

        var user = await FindUserAsync(userId, ct);
        if (user is null)
        {
            return UserErrors.NotFound();
        }

        if (await userManager.IsInRoleAsync(user, nameof(RomdRoleType.Admin)) &&
            !await AccountAccessGuard.HasOtherViableAdminAsync(dbContext, userId, ct))
        {
            return UserErrors.ProtectedUser("The last active administrator cannot be deleted.");
        }

        var result = await userManager.DeleteAsync(user);
        ct.ThrowIfCancellationRequested();
        return result.Succeeded ? Result.Deleted : UserErrors.DeleteFailed();
    }

    public async Task<int> AssignDefaultLibraryAsync(
        int libraryId,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string concurrencyStamp = Guid.NewGuid().ToString();
        int count = await dbContext.Users
            .Where(user => user.LibraryId == null && user.Id != SystemActor.UserId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.LibraryId, libraryId)
                    .SetProperty(user => user.UpdatedAt, updatedAt)
                    .SetProperty(user => user.ConcurrencyStamp, concurrencyStamp),
                ct);
        ct.ThrowIfCancellationRequested();
        return count;
    }

    private async Task<RomdUser?> FindUserAsync(Guid userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var user = await userManager.FindByIdAsync(userId.ToString());
        ct.ThrowIfCancellationRequested();
        return user;
    }

    private async Task<ManagedUser> ToManagedUserAsync(RomdUser user, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var roles = await userManager.GetRolesAsync(user);
        ct.ThrowIfCancellationRequested();
        return Map(user, roles.ToList());
    }

    private IQueryable<UserRoleRow> QueryRows(Guid? userId = null)
    {
        IQueryable<RomdUser> users = dbContext.Users.AsNoTracking();
        if (userId.HasValue)
        {
            users = users.Where(user => user.Id == userId.Value);
        }

        return from user in users
        join userRole in dbContext.UserRoles.AsNoTracking()
            on user.Id equals userRole.UserId into userRoles
        from userRole in userRoles.DefaultIfEmpty()
        join role in dbContext.Roles.AsNoTracking()
            on userRole.RoleId equals role.Id into roles
        from role in roles.DefaultIfEmpty()
        select new UserRoleRow(
            user.Id,
            user.Email ?? string.Empty,
            user.LibraryId,
            user.CreatedAt,
            user.UpdatedAt,
            user.IsSuspended,
            user.RequiresActivation,
            user.LastSignedInAt,
            role == null ? null : role.Name);
    }

    private static IReadOnlyList<ManagedUser> Materialize(IReadOnlyList<UserRoleRow> rows) =>
        rows.GroupBy(row => new { row.Id, row.Email, row.LibraryId, row.CreatedAt, row.UpdatedAt, row.IsSuspended, row.RequiresActivation, row.LastSignedInAt })
            .OrderBy(group => group.Key.Email)
            .Select(group => new ManagedUser(
                group.Key.Id,
                group.Key.Email,
                group.Where(row => row.RoleName is not null).Select(row => row.RoleName!).Order().ToList(),
                group.Key.LibraryId,
                group.Key.CreatedAt,
                group.Key.UpdatedAt,
                group.Key.IsSuspended,
                group.Key.RequiresActivation,
                group.Key.LastSignedInAt))
            .ToList();

    private static ManagedUser Map(RomdUser user, IReadOnlyList<string> roles) =>
        new(
            user.Id,
            user.Email ?? string.Empty,
            roles,
            user.LibraryId,
            user.CreatedAt,
            user.UpdatedAt,
            user.IsSuspended,
            user.RequiresActivation,
            user.LastSignedInAt);

    private sealed record UserRoleRow(
        Guid Id,
        string Email,
        int? LibraryId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        bool IsSuspended,
        bool RequiresActivation,
        DateTimeOffset? LastSignedInAt,
        string? RoleName);
}
