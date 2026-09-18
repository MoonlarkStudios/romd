namespace Romd.Contracts.Management.MetadataProviders;

public sealed record UpdateIgdbProviderSettingsRequest(
    bool Enabled,
    string? ClientId,
    string? ClientSecret,
    bool ClearClientSecret = false)
{
    public Guid Revision { get; init; }
    // This request carries a secret; never include generated record values in diagnostics.
    public override string ToString() => nameof(UpdateIgdbProviderSettingsRequest);
}
