using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

public sealed partial class MediaReconciliationExecution
{
    public async Task<ErrorOr<Success>> RecordVerificationAsync(MediaReconciliationAttempt attempt, RomMediaVerificationMeasurement measured,
        bool processTreeStopped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(measured);
        var capture = attempt.Capture;
        Guid fence = Fence(capture.JobId);
        if (capture.Identity.Operation is not (MediaReconciliationOperation.Verify or MediaReconciliationOperation.Inspect) ||
            !measured.Representation.Measurement.Decoder.Equals(capture.Decoder) || measured.Artifact.Sha256 != measured.Representation.Measurement.ArtifactSha256 ||
            measured.Artifact.Size != measured.Representation.Measurement.ArtifactSize || measured.Artifact.Md5.IsEmpty || measured.Artifact.Sha1.IsEmpty)
            return MediaReconciliationErrors.Measurement;
        var graphs = capture.Sources.Inputs.Select(input => input switch
        {
            MediaReconciliationInput.Artifact artifact => artifact.Graph,
            MediaReconciliationInput.Logical logical => logical.Graph,
            _ => null
        }).Where(graph => graph is not null).Cast<RomRepresentationFacts>().DistinctBy(graph => graph.Id).ToArray();
        if (graphs is not [var captured] || !measured.Representation.MatchesRetained(captured) ||
            (measured is RomMediaVerificationMeasurement.Cd) != (captured.Manifest is RomReconstructionManifest.CdTracks) ||
            capture.Identity.Operation == MediaReconciliationOperation.Inspect && measured is not RomMediaVerificationMeasurement.Whole)
            return MediaReconciliationErrors.Measurement;
        if (measured is RomMediaVerificationMeasurement.Cd observedCd && captured.CdBootInspections.Any(prior => prior.Boot != observedCd.Boot))
            return MediaReconciliationErrors.Measurement;
        if (!processTreeStopped || (await _custody.EnsureJobStoppedAsync(capture.JobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        await using var transaction = await _work.BeginTransactionAsync(ct);
        RequireOwnedTransaction(capture.JobId);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"MediaReconciliationJobs\" WHERE \"JobId\"={capture.JobId} FOR NO KEY UPDATE", ct);
        var demand = await DemandAsync(_db, capture.JobId, ct);
        if (demand is null || demand.CancelRequested || demand.Version < capture.RequestedVersion)
            return MediaReconciliationErrors.Changed;
        var active = await _db.Database.SqlQuery<AttemptRow>($"""
            SELECT "Number","RequestedVersion","Fingerprint","Outcome" FROM romd."MediaReconciliationAttempts" WHERE "JobId"={capture.JobId} AND "Number"={attempt.Number} AND "Outcome" IS NULL
            """).SingleOrDefaultAsync(ct);
        if (active is null || active.RequestedVersion != capture.RequestedVersion || !active.Fingerprint.AsSpan().SequenceEqual(capture.Identity.Fingerprint.AsSpan()))
            return MediaReconciliationErrors.Changed;
        var current = await RefreshSourcesAsync(demand.Subject, capture.Sources, ct);
        if (current is null || current.Fingerprint != capture.Sources.Fingerprint)
            return MediaReconciliationErrors.Changed;
        var retained = (await MediaFactLockSet.ReadAsync(_db, [captured.Id], ct))[captured.Id];
        if (measured is RomMediaVerificationMeasurement.Cd currentCd && retained.CdBootInspections.Any(prior => prior.Boot != currentCd.Boot))
            return MediaReconciliationErrors.Measurement;
        var publisher = new MediaFactPublisher(_db);
        var measurement = measured.Representation.Measurement;
        await new FileRepository(_db).RecordArtifactChecksumsAsync(captured.FileId, measured.Artifact.Sha256, measured.Artifact.Size, measured.Artifact.Md5, measured.Artifact.Sha1, ct);
        bool appended = await publisher.AppendVerificationAsync(captured.Id, measurement, ct);
        Guid proof = appended ? measurement.Id : retained.Verifications.Single(row => row.Decoder.Equals(capture.Decoder)).Id;
        Guid? inspection = null;
        if (measured is RomMediaVerificationMeasurement.Cd { Boot: { } boot } cd)
            await new MediaCreationFactWriter(_db).StageCdBootAsync(captured.Id, proof, cd.InspectorBuild, measurement.ObservedAt, boot, ct);
        else if (measured is RomMediaVerificationMeasurement.Whole whole)
        {
            var output = retained.Outputs.Single();
            if (output.RomId is { } rom)
            {
                var observed = await new LogicalInspectionFactWriter(_db).StageAsync(rom, new(captured.Id, output.Id, captured.FileId, proof), whole.Inspection, ct);
                if (observed.IsError)
                    return observed.Errors;
                inspection = observed.Value.Id;
                if (observed.Value.Created)
                {
                    var requested = await new MediaReconciliationRequests(_db, _clock).StageSubjectAsync(
                        new(observed.Value.Id, new MediaReconciliationSubject.LogicalRom(rom), false, MediaReconciliationCause.Facts, null, null), ct);
                    if (requested.IsError)
                        return requested.Errors;
                }
            }
            if (capture.Identity.Operation == MediaReconciliationOperation.Inspect && inspection is null)
                return MediaReconciliationErrors.Changed;
        }
        long stopped = _clock.GetUtcNow().UtcTicks;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."MediaReconciliationAttempts" SET "ExecutionFenceToken"={fence},"Outcome"='Recorded',"OutcomeCode"='verified',"Deterministic"=false,
                "ResultRepresentationId"={captured.Id},"ResultVerificationId"={proof},"ResultInspectionId"=CAST({(capture.Identity.Operation == MediaReconciliationOperation.Inspect ? inspection : null)} AS uuid),
                "CompletedAtUtcTicks"={stopped},"StoppedAtUtcTicks"={stopped} WHERE "JobId"={capture.JobId} AND "Number"={attempt.Number};
            DELETE FROM romd."MediaReconciliationPins" WHERE "JobId"={capture.JobId} AND "AttemptNumber"={attempt.Number};
            """, ct);
        await transaction.CommitAsync(ct);
        return Result.Success;
    }
}
