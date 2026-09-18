using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Browse;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Account;

public static class ConsumerAccountMapping
{
    public static ConsumerUserSettingsDto ToDto(this ConsumerAccountSettings settings) =>
        new(settings.Theme, settings.ReleasePreference.ToDto());

    public static ConsumerReleasePreferenceDto ToDto(this ConsumerReleasePreference preference) =>
        new(
            preference.PreferredRegionIds.Select(IdCoder.Encode).ToList(),
            preference.PreferredLanguageIds.Select(IdCoder.Encode).ToList(),
            ToDto(preference.RevisionStrategy));

    private static string ToDto(ConsumerRevisionPreference preference) =>
        preference switch
        {
            ConsumerRevisionPreference.NewestFirst => "newestFirst",
            ConsumerRevisionPreference.OldestFirst => "oldestFirst",
            _ => "none"
        };
}
