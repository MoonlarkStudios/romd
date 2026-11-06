using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Account;
using Romd.Persistence.Identity;
using Romd.Persistence;

namespace Romd.Infrastructure.Identity;

internal sealed class ConsumerAccountService(
    RomdDbContext dbContext,
    IPasswordHasher<RomdUser> passwordHasher,
    IConsumerPasswordPolicy passwordPolicy,
    IAccountSessions accountSessions)
    : IConsumerPasswordChanger
{
    public async Task<ErrorOr<IPreparedConsumerPasswordChange>> PrepareAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Password preparation must run outside a transaction.");
        }

        var currentLength = RomdPasswordLengthValidator.ValidateLength(currentPassword);
        if (!currentLength.Succeeded)
            return ConsumerErrors.PasswordChangeFailed(FormatIdentityErrors(currentLength));
        var newLength = RomdPasswordLengthValidator.ValidateLength(newPassword);
        if (!newLength.Succeeded)
            return ConsumerErrors.PasswordChangeFailed(FormatIdentityErrors(newLength));

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, ct);
        if (user is null || user.IsSuspended || user.RequiresActivation)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            return ConsumerErrors.PasswordChangeFailed("Current password is incorrect.");
        }

        var passwordVerification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword);
        if (passwordVerification == PasswordVerificationResult.Failed)
        {
            return ConsumerErrors.PasswordChangeFailed("Current password is incorrect.");
        }

        var passwordValidation = passwordPolicy.Validate(user, newPassword);
        if (!passwordValidation.Succeeded)
        {
            return ConsumerErrors.PasswordChangeFailed(FormatIdentityErrors(passwordValidation));
        }

        string passwordHash = passwordHasher.HashPassword(user, newPassword);
        return ErrorOrFactory.From<IPreparedConsumerPasswordChange>(new PreparedPasswordChange(
            dbContext, accountSessions, userId, user.PasswordHash, user.SecurityStamp, user.ConcurrencyStamp, passwordHash));
    }

    private sealed class PreparedPasswordChange(
        RomdDbContext dbContext,
        IAccountSessions accountSessions,
        Guid userId,
        string? previousHash,
        string? securityStamp,
        string? concurrencyStamp,
        string passwordHash) : IPreparedConsumerPasswordChange
    {
        public async Task<ErrorOr<Updated>> ApplyStagedAsync(CancellationToken ct = default)
        {
            if (dbContext.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException("Password changes require a caller-owned transaction.");
            }

            // Verification ran against the captured snapshot. A reset, suspension, or other
            // account change committed meanwhile must win over these stale credentials.
            int updated = await dbContext.Users
                .Where(candidate => candidate.Id == userId
                    && candidate.ConcurrencyStamp == concurrencyStamp
                    && candidate.SecurityStamp == securityStamp
                    && candidate.PasswordHash == previousHash
                    && !candidate.IsSuspended && !candidate.RequiresActivation)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.PasswordHash, passwordHash)
                    .SetProperty(candidate => candidate.SecurityStamp, Guid.NewGuid().ToString())
                    .SetProperty(candidate => candidate.ConcurrencyStamp, Guid.NewGuid().ToString()), ct);

            if (updated != 1)
                return ConsumerErrors.PasswordChangeFailed("The account changed while updating the password. Try again.");

            await accountSessions.RevokeAsync(userId, null, null, userId, "Password changed", ct);
            return Result.Updated;
        }
    }

    private static string FormatIdentityErrors(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(error => error.Description));
}
