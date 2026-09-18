namespace Romd.Contracts.Management.MetadataProviders;

public sealed record IgdbProviderSettingsDto(
    bool Enabled,
    string? ClientId,
    bool HasClientSecret,
    bool IsConfigured,
    bool ManagedByDeployment,
    string? ConfigurationError,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded,
    string? LastTestMessage)
{
    public Guid Revision { get; init; }
}
