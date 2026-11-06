namespace Romd.Contracts.Consumer.Account;

public sealed record ConsumerUserSettingsDto(
    string Theme,
    ConsumerReleasePreferenceDto ReleasePreference);

/// <summary>
///     Release preferences, naming regions and languages by their registered reference keys. Each list holds at most
///     <see cref="MaximumPreferredKeys" /> distinct keys.
/// </summary>
public sealed record ConsumerReleasePreferenceDto(
    IReadOnlyList<string> PreferredRegionKeys,
    IReadOnlyList<string> PreferredLanguageKeys,
    string RevisionStrategy)
{
    /// <summary>The longest preference list, comfortably above the registered regions or languages.</summary>
    public const int MaximumPreferredKeys = 64;
}
