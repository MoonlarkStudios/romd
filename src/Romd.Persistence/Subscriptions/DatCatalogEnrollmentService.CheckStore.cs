using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Dat;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Subscriptions;

public sealed partial class DatCatalogEnrollmentService
{
    // Even an unchanged existing row must wait for an actual successful owner flush.
    // This observer is scoped with the service/context; receipts also bind to the original transaction.
    private long _checkFlushGeneration;
    private void RecordSuccessfulCheckFlush(object? sender, SavedChangesEventArgs args) => _checkFlushGeneration++;
    public async Task<DatSubscriptionCheckSnapshot?> ReadAsync(int id, CancellationToken ct)
    {
        var row = await _db.DatSubscriptions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        return row is null ? null : Snapshot(row);
    }

    public async Task<IReadOnlyList<DatSubscriptionCheckSnapshot>> ReadCatalogAsync(string catalogId, CancellationToken ct) =>
        (await _db.DatSubscriptions.AsNoTracking().Where(row => row.CatalogId == catalogId).OrderBy(row => row.Id).Take(2).ToListAsync(ct))
        .Select(Snapshot).ToArray();

    public async Task<DatSubscriptionCheckSnapshot?> ReadBySourceAsync(int sourceId, CancellationToken ct)
    {
        var row = await _db.DatSubscriptions.AsNoTracking().SingleOrDefaultAsync(row => row.DatSourceId == sourceId, ct);
        return row is null ? null : Snapshot(row);
    }

    public Task<DatCatalogSubscription> ReadReplayStatusAsync(DatSubscriptionCheckSnapshot snapshot, CancellationToken ct)
    {
        var row = new DatSubscriptionEntity { Id = snapshot.Id, CatalogId = snapshot.CatalogId };
        ApplyValues(row, snapshot.Values);
        return StatusAsync(row, ct);
    }

    public async Task<DatCatalogSubscription> ReadWrittenStatusAsync(IStagedDatSubscriptionCheck write, CancellationToken ct)
    {
        if (write is not StagedIdentity identity || !ReferenceEquals(identity.Context, _db))
            throw new InvalidOperationException("The subscription write belongs to a different context.");
        _ = identity.ReadIdAfterFlush();
        return await StatusAsync(identity.Row, ct);
    }

    public Task<bool> IsCheckDueAsync(int id, DateTimeOffset now, CancellationToken ct) => DueChecks(now).AnyAsync(row => row.Id == id, ct);
    public Task<bool> HasRunningJobAsync(Guid? jobId, CancellationToken ct) => _db.Jobs.AnyAsync(job => job.Id == jobId && !Terminal.Contains(job.Phase), ct);
    public async Task<IReadOnlyList<int>> ReadActiveDatIdsForDocumentAsync(int fileId, int? platformId, CancellationToken ct) =>
        await _db.DatFiles.Where(dat => dat.FileId == fileId && dat.PlatformId == platformId && dat.Lifecycle == "Active")
            .Select(dat => dat.Id).Take(2).ToListAsync(ct);
    public async Task<IReadOnlyList<int>> ReadActiveDatIdsForNameAsync(int platformId, string name, CancellationToken ct) =>
        await _db.DatFiles.Where(dat => dat.PlatformId == platformId && dat.Name == name && dat.Lifecycle == "Active")
            .Select(dat => dat.Id).Take(2).ToListAsync(ct);
    public Task<DatSubscriptionCheckPlatform?> ReadPlatformDefinitionAsync(string systemKey, CancellationToken ct) =>
        _db.Platforms.Where(platform => platform.CanonicalKey == systemKey && platform.Ownership != null)
            .Select(platform => new DatSubscriptionCheckPlatform(platform.Id, platform.Retired)).SingleOrDefaultAsync(ct);
    public Task<bool> IsSourceEnabledAsync(int sourceId, CancellationToken ct) => SourceEnabledAsync(sourceId, ct);

    public Task<bool> TryLockCatalogStagedAsync(string catalogId, CancellationToken ct)
    {
        RequireCheckTransaction();
        return LockCatalogAsync(catalogId, ct);
    }
    public Task<bool> TryLockSourceStagedAsync(int sourceId, CancellationToken ct)
    {
        RequireCheckTransaction();
        return LockSourceAsync(sourceId, ct);
    }

    public Task<IStagedDatSubscriptionCheck> StageNewAsync(string catalogId, DatSubscriptionCheckValues values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireCheckTransaction();
        var row = new DatSubscriptionEntity { CatalogId = catalogId };
        ApplyValues(row, values);
        _db.DatSubscriptions.Add(row);
        return Task.FromResult<IStagedDatSubscriptionCheck>(new StagedIdentity(_db, row, _db.Database.CurrentTransaction!.TransactionId, _checkFlushGeneration, () => _checkFlushGeneration));
    }

    public async Task<IStagedDatSubscriptionCheck> StageExistingAsync(int id, DatSubscriptionCheckValues values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireCheckTransaction();
        var row = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new InvalidOperationException("The locked subscription no longer exists.");
        ApplyValues(row, values);
        return new StagedIdentity(_db, row, _db.Database.CurrentTransaction!.TransactionId, _checkFlushGeneration, () => _checkFlushGeneration);
    }

    private void RequireCheckTransaction()
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Subscription check staging requires a caller-owned transaction.");
    }
    private static DatSubscriptionCheckSnapshot Snapshot(DatSubscriptionEntity row) => new(row.Id, row.CatalogId,
        new(row.DatSourceId, row.PlatformId, row.SystemId, row.ExpectedName, row.State, row.LastCheckedAt,
            row.NextCheckAt, row.ConsecutiveCheckFailures, row.Message, row.CandidateFileId, row.CandidateSha256, row.ActiveSha256, row.JobId));
    private static void ApplyValues(DatSubscriptionEntity row, DatSubscriptionCheckValues values)
    {
        row.DatSourceId = values.DatSourceId;
        row.PlatformId = values.PlatformId;
        row.SystemId = values.SystemId;
        row.ExpectedName = values.ExpectedName;
        row.State = values.State;
        row.LastCheckedAt = values.LastCheckedAt;
        row.NextCheckAt = values.NextCheckAt;
        row.ConsecutiveCheckFailures = values.ConsecutiveCheckFailures;
        row.Message = values.Message;
        row.CandidateFileId = values.CandidateFileId;
        row.CandidateSha256 = values.CandidateSha256;
        row.ActiveSha256 = values.ActiveSha256;
        row.JobId = values.JobId;
    }
    private sealed class StagedIdentity(RomdDbContext db, DatSubscriptionEntity row, Guid transactionId, long stagedGeneration, Func<long> flushGeneration) : IStagedDatSubscriptionCheck
    {
        internal RomdDbContext Context => db;
        internal DatSubscriptionEntity Row => row;
        public int ReadIdAfterFlush()
        {
            if (db.Database.CurrentTransaction?.TransactionId != transactionId || flushGeneration() <= stagedGeneration
                || row.Id == 0 || db.Entry(row).State != EntityState.Unchanged)
                throw new InvalidOperationException("Subscription identity requires an owner flush inside its original transaction.");
            return row.Id;
        }
    }
}
