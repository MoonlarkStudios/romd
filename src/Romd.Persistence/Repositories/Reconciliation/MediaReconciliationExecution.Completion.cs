using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

public sealed partial class MediaReconciliationExecution
{
    public async Task<ErrorOr<Success>> SupersedeAttemptAsync(Guid jobId, string code, bool processTreeStopped, CancellationToken ct = default)
    {
        if (await ReadOutcomeAsync(jobId, ct) is not null)
            return Result.Success;
        Guid fence = Fence(jobId);
        CheckCode(code);
        if (!processTreeStopped || (await _custody.EnsureJobStoppedAsync(jobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        await using var transaction = await _work.BeginTransactionAsync(ct);
        RequireOwnedTransaction(jobId);
        long stopped = _clock.GetUtcNow().UtcTicks;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."MediaReconciliationAttempts" SET "ExecutionFenceToken"={fence},"Outcome"='Superseded',"OutcomeCode"={code},"Deterministic"=false,
                "CompletedAtUtcTicks"={stopped},"StoppedAtUtcTicks"={stopped} WHERE "JobId"={jobId} AND "Outcome" IS NULL;
            DELETE FROM romd."MediaReconciliationPins" pin USING romd."MediaReconciliationAttempts" attempt
                WHERE pin."JobId"={jobId} AND attempt."JobId"=pin."JobId" AND attempt."Number"=pin."AttemptNumber" AND attempt."Outcome" IS NOT NULL;
            """, ct);
        await transaction.CommitAsync(ct);
        return Result.Success;
    }
    public async Task<ErrorOr<Success>> FailAttemptAsync(MediaReconciliationAttempt attempt, string code, bool deterministic, bool processTreeStopped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        CheckCode(code);
        var capture = attempt.Capture;
        Guid fence = Fence(capture.JobId);
        if (!processTreeStopped || (await _custody.EnsureJobStoppedAsync(capture.JobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        await using var transaction = await _work.BeginTransactionAsync(ct);
        RequireOwnedTransaction(capture.JobId);
        long stopped = _clock.GetUtcNow().UtcTicks;
        int updated = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."MediaReconciliationAttempts" SET "ExecutionFenceToken"={fence},"Outcome"='Failed',"OutcomeCode"={code},"Deterministic"={deterministic},
                "CompletedAtUtcTicks"={stopped},"StoppedAtUtcTicks"={stopped}
            WHERE "JobId"={capture.JobId} AND "Number"={attempt.Number} AND "RequestedVersion"={capture.RequestedVersion} AND "Fingerprint"={capture.Identity.Fingerprint.ToArray()} AND "Outcome" IS NULL
            """, ct);
        if (updated != 1)
            return MediaReconciliationErrors.Changed;
        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM romd.\"MediaReconciliationPins\" WHERE \"JobId\"={capture.JobId} AND \"AttemptNumber\"={attempt.Number}", ct);
        if (deterministic)
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."MediaReconciliationFailures" ("Fingerprint","JobId","AttemptNumber") VALUES ({capture.Identity.Fingerprint.ToArray()},{capture.JobId},{attempt.Number})
                ON CONFLICT ("Fingerprint") DO NOTHING
                """, ct);
        await transaction.CommitAsync(ct);
        return Result.Success;
    }
    public async Task<ErrorOr<MediaReconciliationJournalOutcome>> FinishAsync(Guid jobId, long requestedVersion, MediaReconciliationOutcomeKind kind,
        string code, bool processTreeStopped, CancellationToken ct = default)
    {
        if (await ReadOutcomeAsync(jobId, ct) is { } recorded)
            return recorded;
        Guid fence = Fence(jobId);
        CheckCode(code);
        if (!Enum.IsDefined(kind) || kind == MediaReconciliationOutcomeKind.CompletedWithErrors || requestedVersion <= 0)
            throw new ArgumentException("Subject completion requires its exact current demand and outcome.");
        if (!processTreeStopped || (await _custody.EnsureJobStoppedAsync(jobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        await using var transaction = await _work.BeginTransactionAsync(ct);
        RequireOwnedTransaction(jobId);
        var scope = await DemandAsync(_db, jobId, ct);
        if (scope is null)
            return MediaReconciliationErrors.Changed;
        // Acceptance holds this advisory lock without locking the root. Serialize settlement before it takes subject rows,
        // so either the new demand is included or the acceptor observes the completed root and creates fresh work.
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({9416 + (int)scope.Subject.Kind},{scope.Subject.Id})", ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"MediaReconciliationJobs\" WHERE \"JobId\"={jobId} FOR NO KEY UPDATE", ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"MediaReconciliationSubjects\" WHERE \"LatestJobId\"={jobId} FOR UPDATE", ct);
        var demand = await DemandAsync(_db, jobId, ct);
        if (demand is null || demand.Version != requestedVersion || demand.CancelRequested && kind != MediaReconciliationOutcomeKind.Cancelled)
            return MediaReconciliationErrors.Changed;
        var active = await _db.Database.SqlQuery<AttemptRow>($"""
            SELECT "Number","RequestedVersion","Fingerprint","Outcome" FROM romd."MediaReconciliationAttempts" WHERE "JobId"={jobId} AND "Outcome" IS NULL
            """).SingleOrDefaultAsync(ct);
        if (active is not null)
        {
            if (kind == MediaReconciliationOutcomeKind.Completed)
                return MediaReconciliationErrors.Changed;
            string disposition = kind switch { MediaReconciliationOutcomeKind.Cancelled => "Cancelled", MediaReconciliationOutcomeKind.Failed => "Failed", _ => "Superseded" };
            long stopped = _clock.GetUtcNow().UtcTicks;
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE romd."MediaReconciliationAttempts" SET "ExecutionFenceToken"={fence},"Outcome"={disposition},"OutcomeCode"={code},"Deterministic"=false,
                    "CompletedAtUtcTicks"={stopped},"StoppedAtUtcTicks"={stopped} WHERE "JobId"={jobId} AND "Number"={active.Number};
                DELETE FROM romd."MediaReconciliationPins" WHERE "JobId"={jobId} AND "AttemptNumber"={active.Number};
                """, ct);
        }
        var completed = _clock.GetUtcNow();
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."MediaReconciliationOutcomes" ("JobId","Kind","Code","AcknowledgedVersion","ExecutionFenceToken","CompletedAtUtcTicks")
                VALUES ({jobId},{kind.ToString()},{code},{requestedVersion},{fence},{completed.UtcTicks});
            UPDATE romd."MediaReconciliationSubjects" SET "AcknowledgedVersion"={requestedVersion} WHERE "LatestJobId"={jobId};
            UPDATE romd."Jobs" SET "Phase"={kind.ToString()},"CompletedAt"={completed.ToUnixTimeMilliseconds()},"CurrentItem"=NULL,
                "NextAttemptAtUtc"=NULL,"ExecutionFenceToken"=NULL,"ExecutionLeaseExpiresAtUtc"=NULL WHERE "Id"={jobId};
            """, ct);
        await transaction.CommitAsync(ct);
        return new MediaReconciliationJournalOutcome(jobId, kind, code, requestedVersion, completed);
    }
}
