namespace Romd.Contracts.Consumer.Account;

public sealed record UpdateConsumerUserSettingsRequest(
    string? Theme = null,
    ConsumerReleasePreference? ReleasePreference = null);
