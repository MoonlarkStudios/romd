namespace Romd.Contracts.Consumer.Account;

public sealed record UpdateConsumerUserSettingsRequest(
    string Theme,
    ConsumerReleasePreferenceDto? ReleasePreference = null);
