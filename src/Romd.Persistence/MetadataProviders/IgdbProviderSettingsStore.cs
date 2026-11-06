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
        var row = await LockAsync(expectedRevision, ct);
        if (row is null)
            return false;
        row.Enabled = settings.Enabled;
        row.ClientId = settings.ClientId;
        row.ProtectedClientSecret = settings.ProtectedClientSecret;
        ApplyTest(row, settings);
        return true;
    }

    public async Task<bool> RecordTestAsync(Guid expectedRevision, IgdbProviderStoredSettings settings, CancellationToken ct = default)
    {
        var row = await LockAsync(expectedRevision, ct);
        if (row is null)
            return false;
        ApplyTest(row, settings);
        return true;
    }

    private async Task<MetadataProviderSettingsEntity?> LockAsync(Guid expectedRevision, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Settings writes require the command transaction.");
        await _db.Database.ExecuteSqlRawAsync("SELECT 1 FROM romd.\"MetadataProviderSettings\" WHERE \"ProviderId\" = 'igdb' FOR UPDATE", ct);
        var row = await _db.Set<MetadataProviderSettingsEntity>().AsTracking().SingleAsync(x => x.ProviderId == "igdb", ct);
        await _db.Entry(row).ReloadAsync(ct);
        return row.Revision == expectedRevision ? row : null;
    }

    private static void ApplyTest(MetadataProviderSettingsEntity row, IgdbProviderStoredSettings settings)
    {
        row.Revision = settings.Revision;
        row.LastTestedAt = settings.LastTestedAt;
        row.LastTestSucceeded = settings.LastTestSucceeded;
        row.LastTestMessage = settings.LastTestMessage;
        row.TestConfigurationFingerprint = settings.TestConfigurationFingerprint;
    }
}
