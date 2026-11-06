using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories.Reconciliation;

public sealed partial class MediaReconciliationRequests
{
    public async Task<ErrorOr<Success>> StageCancellationAsync(Guid jobId, CancellationToken ct = default)
    {
        RequireTransaction();
        ct.ThrowIfCancellationRequested();
        // Follow worker lock order: root, then definition. No attempt, pin or outcome is changed here.
        var locked = await _db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM romd."Jobs" WHERE "Id"={jobId} AND "JobType"='media-reconciliation' FOR NO KEY UPDATE
            """).ToArrayAsync(ct);
        if (locked.Length == 0)
            return Error.NotFound("MediaReconciliation.Missing", "The media job does not exist.");
        var job = await JobAsync(jobId, ct);
        if (job.IsTerminal)
            return Error.Conflict("MediaReconciliation.AlreadyComplete", "The media job is already complete.");
        var definition = await _db.Set<MediaReconciliationDefinitionEntity>().AsTracking().SingleAsync(row => row.JobId == jobId, ct);
        if (_db.Entry(definition).State == EntityState.Unchanged)
            await _db.Entry(definition).ReloadAsync(ct);
        definition.CancelRequested = true;
        return Result.Success;
    }

    public async Task<ErrorOr<MediaReconciliationAcceptance>> StageRetryAsync(Guid requestId, Guid previousJobId, Guid actorId, CancellationToken ct = default)
    {
        RequireTransaction();
        ct.ThrowIfCancellationRequested();
        if (requestId == Guid.Empty || previousJobId == Guid.Empty || actorId == Guid.Empty)
            throw new ArgumentException("Retry requires exact request, previous job and authenticated actor identities.");
        await LockRequestAsync(requestId, ct);
        if (await PriorAsync(requestId, ct) is { } prior)
            return prior.Cause == MediaReconciliationCause.Retry.ToString() && prior.RetryOfJobId == previousJobId && prior.ActorId == actorId
                ? new MediaReconciliationAcceptance(await JobAsync(prior.JobId, ct), false, false) : _conflict;
        var previous = await Jobs.SingleOrDefaultAsync(row => row.Id == previousJobId, ct);
        if (previous is null)
            return Error.NotFound("MediaReconciliation.Missing", "The media job does not exist.");
        var job = previous.ToDomain();
        if (!job.IsTerminal)
            return Error.Conflict("MediaReconciliation.NotComplete", "Wait for durable settlement before retrying this media job.");
        if (job.Request is MediaReconciliationRequest.SubjectWork subject)
        {
            bool conversion = await _db.Set<MediaReconciliationRequestEntity>().AnyAsync(row => row.JobId == previousJobId && row.ConversionRequested, ct);
            // A retry is an independent demand. It cannot acquire an earlier selection's cancellation rights.
            return await StageSubjectCoreAsync(new(requestId, subject.Subject, conversion, MediaReconciliationCause.Retry, actorId, null), previousJobId, ct);
        }
        var selection = (MediaReconciliationRequest.SelectionWork)job.Request;
        return await StageSelectionCoreAsync(requestId, selection.Filter, selection.Limit, selection.ConversionRequested,
            MediaReconciliationCause.Retry, actorId, selection.Bounds, previousJobId, ct);
    }
}
