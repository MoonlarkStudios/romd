namespace Romd.Contracts.Consumer.Account;

public sealed record ConsumerUserSettingsDto(
    string Theme,
    ConsumerReleasePreferenceDto ReleasePreference);

public sealed record ConsumerReleasePreferenceDto(
    IReadOnlyList<string> PreferredRegionIds,
    IReadOnlyList<string> PreferredLanguageIds,
    string RevisionStrategy);
