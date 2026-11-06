using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Repositories;

public sealed class JobCancellationStore(RomdDbContext context, IJobRepository jobs) : IJobCancellationStore
{
    private Job? _lockedJob;
    private Guid? _lockedTransactionId;
    private static readonly string[] TerminalPhases = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred", "Superseded"];

    public async Task<Job?> LockVisibleForCancellationStagedAsync(
        Guid jobId, JobVisibilityScope visibility, CancellationToken ct = default)
    {
        var transaction = RequireTransaction();
        _lockedJob = null;
        _lockedTransactionId = null;
        if (visibility.Empty)
            return null;
        bool restrictOwner = visibility.OwnerId.HasValue;
        var ownerId = visibility.OwnerId.GetValueOrDefault();
        var adminOnlyTypes = JobVisibility.AdminOnlyJobTypes;
        // A non-key lock matches worker/media ordering and remains compatible with FK key-share.
        // No title, file, selection or custody lock precedes this root lock.
        var locked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM romd."Jobs"
            WHERE "Id" = {jobId}
              AND (NOT {restrictOwner} OR "CreatedByUserId" = {ownerId})
              AND ({visibility.IncludeAdminOnly} OR "JobType" <> ALL({adminOnlyTypes}))
            FOR NO KEY UPDATE
            """).ToArrayAsync(ct);
        if (locked.Length == 0)
            return null;
        var current = await jobs.GetByIdAsync(jobId, ct);
        if (current is null || !JobVisibility.IsVisibleTo(current, visibility))
            return null;
        _lockedJob = current;
        _lockedTransactionId = transaction.TransactionId;
        return current;
    }

    public async Task ApplyCancellationStagedAsync(Job job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var transaction = RequireTransaction();
        if (!ReferenceEquals(job, _lockedJob) || _lockedTransactionId != transaction.TransactionId)
            throw new InvalidOperationException("Cancellation requires this transaction's locked current job.");
        if (job is MetadataRematerializationJob or MediaReconciliationJob || job.Phase != "Cancelled" || job.CompletedAt is null)
            throw new InvalidOperationException("An ordinary job's cancellation transition is required.");
        int updated = await context.Jobs.Where(row => row.Id == job.Id && !TerminalPhases.Contains(row.Phase))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Phase, job.Phase)
                .SetProperty(row => row.CompletedAt, job.CompletedAt)
                .SetProperty(row => row.UpdatedAt, job.CompletedAt.Value)
                .SetProperty(row => row.ExecutionFenceToken, (Guid?)null)
                .SetProperty(row => row.ExecutionLeaseExpiresAtUtc, (DateTimeOffset?)null), ct);
        if (updated != 1)
            throw new InvalidOperationException("Cancellation requires an existing non-terminal locked job.");
        if (job is ArtworkImportJob artwork)
            await context.ArtworkSelections.Where(selection => selection.TitleId == artwork.TitleId && selection.Role == artwork.Role
                    && selection.Revision == artwork.SelectionRevision && selection.PendingRequestId == artwork.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(selection => selection.PendingRequestId, (Guid?)null), ct);
        _lockedJob = null;
        _lockedTransactionId = null;
    }

    private IDbContextTransaction RequireTransaction() =>
        context.Database.CurrentTransaction is { } transaction && transaction.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted
            ? transaction
            : throw new InvalidOperationException("Job cancellation requires the caller's ReadCommitted transaction.");
}
