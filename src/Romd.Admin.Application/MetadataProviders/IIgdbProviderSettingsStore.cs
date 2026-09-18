namespace Romd.Admin.Application.MetadataProviders;

public interface IIgdbProviderSettingsStore
{
    Task<IgdbProviderStoredSettings> LoadAsync(CancellationToken ct = default);
    Task<bool> TryUpdateAsync(Guid expectedRevision, IgdbProviderStoredSettings settings, CancellationToken ct = default);
    Task RecordTestAsync(Guid expectedRevision, DateTimeOffset testedAt, bool succeeded,
        string message, string configurationFingerprint, CancellationToken ct = default);
}

public sealed record IgdbProviderStoredSettings(
    bool Enabled,
    string? ClientId,
    string? ProtectedClientSecret,
    Guid Revision,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    string? TestConfigurationFingerprint);
