using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Identity;

public sealed class ConsumerUserSettingsStore : IConsumerUserSettingsStore
{
    private const string DefaultTheme = "system";
    private readonly RomdDbContext dbContext;
    private readonly TimeProvider timeProvider;
    private readonly IConsumerTaxonomyKeys taxonomyKeys;

    public ConsumerUserSettingsStore(RomdDbContext dbContext, TimeProvider timeProvider,
        IConsumerTaxonomyKeys taxonomyKeys)
    {
        this.dbContext = dbContext;
        this.timeProvider = timeProvider;
        this.taxonomyKeys = taxonomyKeys;
    }

    public async Task<ConsumerAccountSettings> GetSettingsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var entity = await dbContext.Set<ConsumerUserSettingsEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.UserId == userId, ct);

        // Stored ids of rows merged away or never registered are ignored, here and by release selection.
        return entity is null
            ? new ConsumerAccountSettings(DefaultTheme)
            : new ConsumerAccountSettings(entity.Theme, await ToReleasePreference(entity).RegisteredOnlyAsync(taxonomyKeys, ct));
    }

    public async Task<ConsumerAccountSettings> SaveSettingsAsync(
        Guid userId,
        ConsumerAccountSettings settings,
        CancellationToken ct = default)
    {
        // Raw SQL must use the same Unix-millisecond representation as the EF timestamp converter.
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        string preferredRegionIdsJson = SerializeIds(settings.ReleasePreference.PreferredRegionIds);
        string preferredLanguageIdsJson = SerializeIds(settings.ReleasePreference.PreferredLanguageIds);
        string revisionPreference = settings.ReleasePreference.RevisionStrategy.ToString();

        // One atomic upsert also handles two devices saving this user's first settings at once.
        // Preserve CreatedAt on later saves and replace the complete preference snapshot together.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."ConsumerUserSettings"
                ("UserId", "Theme", "PreferredRegionIdsJson", "PreferredLanguageIdsJson",
                 "RevisionPreference", "CreatedAt")
            VALUES ({userId}, {settings.Theme}, {preferredRegionIdsJson}, {preferredLanguageIdsJson},
                    {revisionPreference}, {now})
            ON CONFLICT ("UserId") DO UPDATE SET
                "Theme" = EXCLUDED."Theme",
                "PreferredRegionIdsJson" = EXCLUDED."PreferredRegionIdsJson",
                "PreferredLanguageIdsJson" = EXCLUDED."PreferredLanguageIdsJson",
                "RevisionPreference" = EXCLUDED."RevisionPreference",
                "UpdatedAt" = EXCLUDED."CreatedAt"
            """, ct);

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
}
