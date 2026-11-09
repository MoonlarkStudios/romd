using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Artwork;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;

using ArtworkCompletionJob = Romd.Domain.Jobs.ArtworkCompletionJob;

namespace Romd.Persistence.Artwork;

public sealed partial class ArtworkCompletionStore
{
    public async Task<string?> ReadRetryModeAsync(Guid jobId, CancellationToken ct)
    {
        var original = await db.Set<ArtworkCompletionJobEntity>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == jobId, ct);
        return original is null ? null : Mode(original);
    }

    public Task LockRequestAsync(Guid requestId, CancellationToken ct)
    {
        RequireAcceptanceTransaction();
        return JobAcceptanceLock.AcquireAsync(db, 146, requestId.GetHashCode(), ct);
    }

    public async Task<ArtworkCompletionRequestIdentity?> FindRequestAsync(Guid requestId, CancellationToken ct)
    {
        RequireAcceptanceTransaction();
        var existing = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(row => row.Id == requestId, ct);
        return existing is null ? null : new(existing.Id, (existing as ArtworkCompletionJobEntity)?.ScopeJson);
    }

    public Task LockOwnersAsync(CancellationToken ct)
    {
        RequireAcceptanceTransaction();
        return JobAcceptanceLock.AcquireAsync(db, 148, 1, ct);
    }

    public async Task<Guid?> FindActiveSelectedAsync(string signature, CancellationToken ct)
    {
        RequireAcceptanceTransaction();
        return await db.Set<ArtworkCompletionJobEntity>().AsNoTracking().Where(row => row.ScopeJson == signature && Active.Contains(row.Phase) &&
                !db.Set<ArtworkCompletionItemEntity>().Any(item => item.JobId == row.Id && item.RequireTracked))
            .Select(row => (Guid?)row.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<ErrorOr<ArtworkCompletionScopeSnapshot>> SnapshotScopeAsync(ArtworkCompletionRequest request, CancellationToken ct)
    {
        RequireAcceptanceTransaction();
        var scope = await ScopeAsync(request, ct);
        if (scope.IsError)
            return scope.Errors;
        var titles = await scope.Value.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new ArtworkCompletionTitle(row.Id, row.Name)).ToListAsync(ct);
        var trackedRetryIds = request.Scope == "Retry"
            ? await db.Set<ArtworkCompletionItemEntity>().AsNoTracking().Where(item => item.JobId == request.RetryJobId && item.RequireTracked)
                .Select(item => item.TitleId).ToListAsync(ct) : [];
        return new ArtworkCompletionScopeSnapshot(titles, trackedRetryIds);
    }

    public async Task<IReadOnlyList<ArtworkCompletionOwner>> ReadOwnersAsync(IReadOnlyList<int> titleIds, CancellationToken ct)
    {
        RequireAcceptanceTransaction();
        var owners = await (from item in db.Set<ArtworkCompletionItemEntity>().AsNoTracking()
                            join job in db.Set<ArtworkCompletionJobEntity>().AsNoTracking() on item.JobId equals job.Id
                            where titleIds.Contains(item.TitleId) && item.CompletedAt == null && item.FollowUpJobId == null && Active.Contains(job.Phase)
                            orderby job.Phase == "Pending" descending, job.CreatedAt descending, job.Id
                            select new { Item = item, JobId = job.Id }).ToListAsync(ct);
        var ids = owners.Select(owner => owner.JobId).Distinct().ToArray();
        var modes = await db.Set<ArtworkCompletionJobEntity>().AsNoTracking().Where(row => ids.Contains(row.Id)).ToDictionaryAsync(row => row.Id, Mode, ct);
        return Array.AsReadOnly(owners.Select(owner => new ArtworkCompletionOwner(owner.Item.TitleId, owner.JobId,
            modes[owner.JobId], owner.Item.RequireTracked)).ToArray());
    }

    public IStagedArtworkCompletionAcceptance AddJobStaged(ArtworkCompletionJob job, string signature,
        IReadOnlyList<ArtworkCompletionPlannedItem> items)
    {
        RequireAcceptanceTransaction();
        if (job.Id == Guid.Empty || job.Phase != "Pending" || job.Total != items.Count ||
            job.Processed != items.Count(item => item.FollowUpJobId is not null))
            throw new ArgumentException("Stage a new completion job matching its immutable item plan.", nameof(job));
        var row = ArtworkCompletionJobEntity.FromDomain(job);
        row.ScopeJson = signature;
        db.Add(row);
        return new StagedAcceptance(this, db, row, items);
    }

    public async Task InsertItemsStagedAsync(IStagedArtworkCompletionAcceptance receipt, DateTimeOffset acceptedAt, CancellationToken ct)
    {
        var staged = OwnReceipt(receipt);
        var id = staged.ReadAfterFlush();
        var titleIds = staged.Items.Select(item => item.TitleId).ToArray();
        var names = staged.Items.Select(item => item.TitleName).ToArray();
        var tracked = staged.Items.Select(item => item.RequireTracked).ToArray();
        var owners = staged.Items.Select(item => item.FollowUpJobId).ToArray();
        var completedAt = acceptedAt.ToUnixTimeMilliseconds();
        // Keep large scopes set-based; the owner has already flushed the job and its durable dispatch.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."ArtworkCompletionItems" ("JobId", "TitleId", "TitleName", "RequireTracked", "FollowUpJobId", "CompletedAt")
            SELECT {id}, i.title_id, i.title_name, i.require_tracked, i.owner_id,
                CASE WHEN i.owner_id IS NOT NULL THEN {completedAt} ELSE NULL END
            FROM unnest({titleIds}, {names}, {tracked}, {owners}) AS i(title_id, title_name, require_tracked, owner_id)
            """, ct);
    }

    public Task AttachRetryStagedAsync(IStagedArtworkCompletionAcceptance receipt, Guid originalJobId, CancellationToken ct)
    {
        var staged = OwnReceipt(receipt);
        var id = staged.ReadAfterFlush();
        return Projection().AttachRetryAsync(originalJobId, staged.Items.Select(item => item.TitleId).ToArray(), id, ct);
    }

    private StagedAcceptance OwnReceipt(IStagedArtworkCompletionAcceptance receipt)
    {
        RequireAcceptanceTransaction();
        return receipt is StagedAcceptance staged && ReferenceEquals(staged.Owner, this)
            ? staged : throw new InvalidOperationException("Use the completion receipt from this acceptance adapter and transaction.");
    }

    private void RequireAcceptanceTransaction()
    {
        if (db.Database.CurrentTransaction is not { } transaction || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Completion acceptance requires the caller's ReadCommitted transaction.");
    }

    private sealed class StagedAcceptance : IStagedArtworkCompletionAcceptance
    {
        private readonly RomdDbContext _context;
        private readonly ArtworkCompletionJobEntity _row;
        private readonly Guid _transaction;
        internal ArtworkCompletionStore Owner { get; }
        internal IReadOnlyList<ArtworkCompletionPlannedItem> Items { get; }

        internal StagedAcceptance(ArtworkCompletionStore owner, RomdDbContext context, ArtworkCompletionJobEntity row,
            IReadOnlyList<ArtworkCompletionPlannedItem> items)
        {
            Owner = owner;
            _context = context;
            _row = row;
            _transaction = context.Database.CurrentTransaction!.TransactionId;
            Items = Array.AsReadOnly(items.ToArray());
        }

        public Guid ReadAfterFlush()
        {
            if (_context.Database.CurrentTransaction?.TransactionId != _transaction || _context.Entry(_row).State != EntityState.Unchanged ||
                !_context.ChangeTracker.Entries<JobDispatchEntity>().Any(entry => entry.Entity.JobId == _row.Id && entry.State == EntityState.Unchanged))
                throw new InvalidOperationException("Read completion acceptance only after owner flush in its original transaction.");
            return _row.Id;
        }
    }
}
