using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MetadataProviders;

public sealed class IgdbProviderSettingsStore : IIgdbProviderSettingsStore
{
    private readonly RomdDbContext _db;

    public IgdbProviderSettingsStore(RomdDbContext db) => _db = db;

    public Task<IgdbProviderStoredSettings> LoadAsync(CancellationToken ct = default) =>
        _db.Set<MetadataProviderSettingsEntity>().AsNoTracking()
            .Where(x => x.ProviderId == "igdb")
            .Select(x => new IgdbProviderStoredSettings(x.Enabled, x.ClientId, x.ProtectedClientSecret,
                x.Revision, x.LastTestedAt, x.LastTestSucceeded, x.LastTestMessage, x.TestConfigurationFingerprint))
            .SingleAsync(ct);

    public async Task<bool> TryUpdateAsync(Guid expectedRevision, IgdbProviderStoredSettings settings, CancellationToken ct = default)
    {
        var row = await _db.Set<MetadataProviderSettingsEntity>().AsTracking()
            .SingleOrDefaultAsync(item => item.ProviderId == "igdb" && item.Revision == expectedRevision, ct);
        if (row is null) return false;
        row.Enabled = settings.Enabled;
        row.ClientId = settings.ClientId;
        row.ProtectedClientSecret = settings.ProtectedClientSecret;
        row.Revision = settings.Revision;
        row.LastTestedAt = null;
        row.LastTestSucceeded = null;
        row.LastTestMessage = null;
        row.TestConfigurationFingerprint = null;
        return await ConfigurationSave.TrySaveAsync(_db, row, ct);
    }

    public async Task RecordTestAsync(Guid expectedRevision, DateTimeOffset testedAt, bool succeeded,
        string message, string configurationFingerprint, CancellationToken ct = default) =>
        await _db.Set<MetadataProviderSettingsEntity>()
            .Where(x => x.ProviderId == "igdb" && x.Revision == expectedRevision &&
                        (x.LastTestedAt == null || x.LastTestedAt <= testedAt))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastTestedAt, testedAt)
                .SetProperty(x => x.LastTestSucceeded, succeeded)
                .SetProperty(x => x.LastTestMessage, message)
                .SetProperty(x => x.TestConfigurationFingerprint, configurationFingerprint), ct);
}
