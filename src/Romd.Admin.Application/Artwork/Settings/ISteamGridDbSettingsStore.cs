namespace Romd.Admin.Application.Artwork.Settings;

public interface ISteamGridDbSettingsStore
{
    Task<SteamGridDbStoredSettings> LoadAsync(CancellationToken ct = default);
    /// <summary>Requires a caller-owned transaction and stages the version-checked settings replacement.</summary>
    Task<bool> StageUpdateAsync(Guid expectedRevision, SteamGridDbStoredSettings settings, CancellationToken ct = default);
    /// <summary>Requires a caller-owned transaction and returns the staged status's own representation, or null after a lost CAS.</summary>
    Task<SteamGridDbStoredSettings?> StageTestAsync(SteamGridDbTestResult result, CancellationToken ct = default);
}

public sealed record SteamGridDbStoredSettings(bool Enabled, string? ProtectedClientSecret, Guid Revision,
    DateTimeOffset? LastTestedAt, bool? LastTestSucceeded, string? LastTestMessage, string? TestConfigurationFingerprint);
