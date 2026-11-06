namespace Romd.Contracts.Management.Artwork;

public sealed record SteamGridDbSettingsDto(Guid Revision, bool Enabled, bool HasApiKey, bool IsConfigured,
    bool ManagedByDeployment, string? ConfigurationError, DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded, string? LastTestMessage);
