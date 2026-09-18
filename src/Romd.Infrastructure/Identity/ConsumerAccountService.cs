using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Identity;

internal sealed class ConsumerAccountService(
    RomdDbContext dbContext,
    IPasswordHasher<RomdUser> passwordHasher,
    IConsumerPasswordPolicy passwordPolicy,
    TimeProvider timeProvider,
    Romd.Application.Common.Security.IAccountSessions accountSessions)
    : IConsumerPasswordChanger,
        IConsumerUserSettingsStore
{
    private const string DefaultTheme = "system";

    public async Task<ErrorOr<Updated>> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, ct);
        if (user is null)
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
        int updated = await dbContext.Users
            .Where(candidate => candidate.Id == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.PasswordHash, passwordHash)
                .SetProperty(candidate => candidate.SecurityStamp, Guid.NewGuid().ToString())
                .SetProperty(candidate => candidate.ConcurrencyStamp, Guid.NewGuid().ToString()), ct);

        if (updated == 1) await accountSessions.RevokeAsync(userId, null, null, userId, "Password changed", ct);
        return updated == 1
            ? Result.Updated
            : ConsumerErrors.CurrentUserRequired();
    }

    public async Task<ConsumerAccountSettings> GetSettingsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var entity = await dbContext.Set<ConsumerUserSettingsEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.UserId == userId, ct);

        return entity is null
            ? new ConsumerAccountSettings(DefaultTheme)
            : new ConsumerAccountSettings(entity.Theme, ToReleasePreference(entity));
    }

    public async Task<ConsumerAccountSettings> SaveSettingsAsync(
        Guid userId,
        ConsumerAccountSettings settings,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        string preferredRegionIdsJson = SerializeIds(settings.ReleasePreference.PreferredRegionIds);
        string preferredLanguageIdsJson = SerializeIds(settings.ReleasePreference.PreferredLanguageIds);
        string revisionPreference = settings.ReleasePreference.RevisionStrategy.ToString();

        int updated = await dbContext.Set<ConsumerUserSettingsEntity>()
            .Where(current => current.UserId == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(current => current.Theme, settings.Theme)
                .SetProperty(current => current.PreferredRegionIdsJson, preferredRegionIdsJson)
                .SetProperty(current => current.PreferredLanguageIdsJson, preferredLanguageIdsJson)
                .SetProperty(current => current.RevisionPreference, revisionPreference)
                .SetProperty(current => current.UpdatedAt, now), ct);

        if (updated == 0)
        {
            dbContext.Set<ConsumerUserSettingsEntity>().Add(new ConsumerUserSettingsEntity
            {
                UserId = userId,
                Theme = settings.Theme,
                PreferredRegionIdsJson = preferredRegionIdsJson,
                PreferredLanguageIdsJson = preferredLanguageIdsJson,
                RevisionPreference = revisionPreference,
                CreatedAt = now
            });

            await dbContext.SaveChangesAsync(ct);
        }

        return settings;
    }

    private static ConsumerReleasePreference ToReleasePreference(ConsumerUserSettingsEntity entity) =>
        new(
            DeserializeIds(entity.PreferredRegionIdsJson),
            DeserializeIds(entity.PreferredLanguageIdsJson),
            Enum.TryParse<ConsumerRevisionPreference>(entity.RevisionPreference, out var preference)
                ? preference
                : ConsumerRevisionPreference.None);

    private static string SerializeIds(IReadOnlyList<int> ids) =>
        JsonSerializer.Serialize(ids);

    private static IReadOnlyList<int> DeserializeIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FormatIdentityErrors(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(error => error.Description));
}
