using Romd.Consumer.Application.Browse;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Account;

public static class ConsumerAccountMapping
{
    /// <summary>
    ///     Maps preference ids to their keys. The settings store already keeps only ids that name registered rows
    ///     (<see cref="RegisteredOnlyAsync" />), and nothing deletes or unregisters a registered row (a merge refuses one
    ///     as its source). Dropping an id without a key is only a guard, so a broken invariant never fails the read.
    /// </summary>
    public static async Task<ConsumerUserSettingsDto> ToDtoAsync(
        this ConsumerAccountSettings settings,
        IConsumerTaxonomyKeys keys,
        CancellationToken ct)
    {
        var preference = settings.ReleasePreference;
        var regionKeys = await keys.RegionKeysAsync(preference.PreferredRegionIds, ct);
        var languageKeys = await keys.LanguageKeysAsync(preference.PreferredLanguageIds, ct);
        return new(settings.Theme, new ConsumerReleasePreferenceDto(
            KeysOf(preference.PreferredRegionIds, regionKeys),
            KeysOf(preference.PreferredLanguageIds, languageKeys),
            ToDto(preference.RevisionStrategy)));
    }

    /// <summary>
    ///     The preference with only the ids that still name registered rows, each once, in order: the one
    ///     registered-only filter, applied by the settings store, so the settings read and release selection both see
    ///     these ids. Repeats are dropped so the read honors the keys' published uniqueness.
    /// </summary>
    public static async Task<ConsumerReleasePreference> RegisteredOnlyAsync(
        this ConsumerReleasePreference preference,
        IConsumerTaxonomyKeys keys,
        CancellationToken ct)
    {
        var regionKeys = await keys.RegionKeysAsync(preference.PreferredRegionIds, ct);
        var languageKeys = await keys.LanguageKeysAsync(preference.PreferredLanguageIds, ct);
        return preference with
        {
            PreferredRegionIds = preference.PreferredRegionIds.Where(regionKeys.ContainsKey).Distinct().ToList(),
            PreferredLanguageIds = preference.PreferredLanguageIds.Where(languageKeys.ContainsKey).Distinct().ToList()
        };
    }

    private static List<string> KeysOf(IReadOnlyList<int> ids, IReadOnlyDictionary<int, string> keys) =>
        ids.Where(keys.ContainsKey).Select(id => keys[id]).ToList();

    private static string ToDto(ConsumerRevisionPreference preference) =>
        preference switch
        {
            ConsumerRevisionPreference.NewestFirst => "newestFirst",
            ConsumerRevisionPreference.OldestFirst => "oldestFirst",
            _ => "none"
        };
}
