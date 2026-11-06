using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Artwork;

public sealed class SteamGridDbSettingsStore : ISteamGridDbSettingsStore
{
    private readonly RomdDbContext _db;

    public SteamGridDbSettingsStore(RomdDbContext db) => _db = db;

    public Task<SteamGridDbStoredSettings> LoadAsync(CancellationToken ct = default) =>
        _db.Set<MetadataProviderSettingsEntity>().AsNoTracking()
            .Where(x => x.ProviderId == "steamgriddb")
            .Select(x => new SteamGridDbStoredSettings(x.Enabled, x.ProtectedClientSecret,
                x.Revision, x.LastTestedAt, x.LastTestSucceeded, x.LastTestMessage, x.TestConfigurationFingerprint))
            .SingleAsync(ct);

    public async Task<bool> StageUpdateAsync(Guid expectedRevision, SteamGridDbStoredSettings settings, CancellationToken ct = default)
    {
        var row = await LockAsync(expectedRevision, ct);
        if (row is null)
            return false;
        row.Enabled = settings.Enabled;
        row.ProtectedClientSecret = settings.ProtectedClientSecret;
        row.Revision = settings.Revision;
        row.LastTestedAt = null;
        row.LastTestSucceeded = null;
        row.LastTestMessage = null;
        row.TestConfigurationFingerprint = null;
        return true;
    }

    public async Task<SteamGridDbStoredSettings?> StageTestAsync(SteamGridDbTestResult result, CancellationToken ct = default)
    {
        var row = await LockAsync(result.ExpectedRevision, ct);
        if (row is null || row.LastTestedAt > result.TestedAt)
            return null;
        row.Revision = Guid.NewGuid();
        row.LastTestedAt = result.TestedAt;
        row.LastTestSucceeded = result.Succeeded;
        row.LastTestMessage = result.Message;
        row.TestConfigurationFingerprint = result.ConfigurationFingerprint;
        return new(row.Enabled, row.ProtectedClientSecret, row.Revision, row.LastTestedAt, row.LastTestSucceeded,
            row.LastTestMessage, row.TestConfigurationFingerprint);
    }

    private async Task<MetadataProviderSettingsEntity?> LockAsync(Guid expectedRevision, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Settings writes require a caller-owned transaction.");
        // The no-op CAS locks the matching row until the handler commits; a competing revision cannot be staged.
        var affected = await _db.Set<MetadataProviderSettingsEntity>()
            .Where(row => row.ProviderId == "steamgriddb" && row.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Revision, expectedRevision), ct);
        if (affected != 1)
            return null;
        var row = await _db.Set<MetadataProviderSettingsEntity>().AsTracking().SingleAsync(item => item.ProviderId == "steamgriddb", ct);
        await _db.Entry(row).ReloadAsync(ct);
        return row;
    }
}
