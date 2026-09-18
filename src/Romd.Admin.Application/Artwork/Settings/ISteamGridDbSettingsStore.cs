namespace Romd.Admin.Application.Artwork.Settings;

public interface ISteamGridDbSettingsStore
{
    Task<SteamGridDbStoredSettings> LoadAsync(CancellationToken ct = default);
    Task<bool> TryUpdateAsync(Guid expectedRevision, SteamGridDbStoredSettings settings, CancellationToken ct = default);
    Task RecordTestAsync(Guid expectedRevision, DateTimeOffset testedAt, bool succeeded,
        string message, string configurationFingerprint, CancellationToken ct = default);
}

public sealed record SteamGridDbStoredSettings(bool Enabled, string? ProtectedClientSecret, Guid Revision,
    DateTimeOffset? LastTestedAt, bool? LastTestSucceeded, string? LastTestMessage, string? TestConfigurationFingerprint);
