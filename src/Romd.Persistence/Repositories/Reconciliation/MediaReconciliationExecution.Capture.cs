using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

public sealed partial class MediaReconciliationExecution
{
    private sealed class AttemptRow
    {
        public int Number { get; set; }
        public long RequestedVersion { get; set; }
        public byte[] Fingerprint { get; set; } = null!;
        public string? Outcome { get; set; }
    }
    private sealed class FailureRow
    {
        public Guid JobId { get; set; }
        public int AttemptNumber { get; set; }
        public string Code { get; set; } = null!;
    }
    public async Task<ErrorOr<MediaReconciliationAttempt>> CaptureAsync(MediaReconciliationCapture capture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Guid fence = Fence(capture.JobId);
        if (_db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Execution capture owns its transaction; media work cannot borrow one.");
        if ((await _custody.EnsureJobStoppedAsync(capture.JobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        await using var transaction = await _work.BeginTransactionAsync(ct);
        RequireOwnedTransaction(capture.JobId);
        var demand = await DemandAsync(_db, capture.JobId, ct);
        if (demand is null || demand.CancelRequested || demand.Version != capture.RequestedVersion)
            return MediaReconciliationErrors.Changed;
        if (demand.Subject is MediaReconciliationSubject.Representation && capture.Identity.Operation != MediaReconciliationOperation.Verify ||
            (demand.Subject is MediaReconciliationSubject.OriginalLayout or MediaReconciliationSubject.CatalogCdLayout) != (capture.Sources.Layout is not null) ||
            capture.Sources.Layout is { } scoped && !demand.Subject.Equals(scoped.Subject))
            return MediaReconciliationErrors.Changed;
        if (capture.Identity.Operation is MediaReconciliationOperation.Encode or MediaReconciliationOperation.Release or MediaReconciliationOperation.Adopt)
        {
            ErrorOr<MediaReconciliationFacts> current;
            try
            { current = await _authority.CheckAsync(capture, demand, [], ct); }
            catch (MediaReconciliationLockScopeChangedException) { return MediaReconciliationErrors.Changed; }
            if (current.IsError)
                return current.Errors;
        }
        var refreshed = await RefreshSourcesAsync(demand.Subject, capture.Sources, ct);
        if (refreshed is null || refreshed.Fingerprint != capture.Sources.Fingerprint)
            return MediaReconciliationErrors.Changed;
        var attempts = await _db.Database.SqlQuery<AttemptRow>($"""
            SELECT "Number","RequestedVersion","Fingerprint","Outcome" FROM romd."MediaReconciliationAttempts" WHERE "JobId"={capture.JobId} ORDER BY "Number"
            """).ToArrayAsync(ct);
        var active = attempts.SingleOrDefault(row => row.Outcome is null);
        int number;
        if (active is not null && active.RequestedVersion == capture.RequestedVersion && active.Fingerprint.AsSpan().SequenceEqual(capture.Identity.Fingerprint.AsSpan()))
        {
            number = active.Number;
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"MediaReconciliationAttempts\" SET \"ExecutionFenceToken\"={fence} WHERE \"JobId\"={capture.JobId} AND \"Number\"={number}", ct);
        }
        else
        {
            if (active is not null)
            {
                long stopped = _clock.GetUtcNow().UtcTicks;
                await _db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE romd."MediaReconciliationAttempts" SET "ExecutionFenceToken"={fence},"Outcome"='Superseded',"OutcomeCode"='replanned',"Deterministic"=false,
                        "CompletedAtUtcTicks"={stopped},"StoppedAtUtcTicks"={stopped} WHERE "JobId"={capture.JobId} AND "Number"={active.Number};
                    DELETE FROM romd."MediaReconciliationPins" WHERE "JobId"={capture.JobId} AND "AttemptNumber"={active.Number};
                    """, ct);
            }
            number = checked((attempts.LastOrDefault()?.Number ?? 0) + 1);
            var native = (capture.Decoder as RomDecoderIdentity.Native)?.Build;
            string decoder = native?.BuildLabel ?? ((RomDecoderIdentity.Managed)capture.Decoder).Build;
            var source = capture.Sources;
            var identity = capture.Identity;
            var layout = source.Layout;
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."MediaReconciliationAttempts" ("JobId","Number","ExecutionFenceToken","RequestedVersion","Operation","KindId","RecipeId",
                    "SourceFingerprint","CapabilityFingerprint","LimitsFingerprint","Fingerprint","SourceCount","DecoderBuild","DecoderRid","DecoderUpstreamCommit","DecoderRecipeVersion",
                    "EncoderBuild","DiscKind","OriginalLayoutId","CatalogFileId","CatalogBindingId","LayoutRevision","PlatformId","ReleaseId","CatalogFingerprint","EntryPoint","LogicalInspectionIds","ReferenceFingerprint","StartedAtUtcTicks")
                VALUES ({capture.JobId},{number},{fence},{capture.RequestedVersion},{identity.Operation.ToString()},CAST({capture.KindId} AS text),CAST({identity.Recipe?.Value} AS text),
                    {identity.SourceFingerprint.ToArray()},{identity.CapabilityFingerprint.ToArray()},{identity.LimitsFingerprint.ToArray()},{identity.Fingerprint.ToArray()},{source.Inputs.Count},
                    {decoder},CAST({native?.Rid} AS text),CAST({native?.UpstreamCommit.ToArray()} AS bytea),CAST({native?.RecipeVersion} AS integer),
                    CAST({capture.EncoderBuild} AS text),CAST({capture.DiscKind?.ToString()} AS text),CAST({(layout as MediaReconciliationLayout.Original)?.LayoutId} AS integer),
                    CAST({(layout as MediaReconciliationLayout.Catalog)?.Requirement.CatalogFileId} AS integer),CAST({(layout as MediaReconciliationLayout.Catalog)?.BindingId} AS integer),CAST({layout?.Revision} AS uuid),
                    CAST({layout?.PlatformId} AS integer),CAST({layout?.ReleaseId} AS integer),CAST({layout?.CatalogFingerprint} AS text),CAST({layout?.EntryPoint} AS text),
                    {source.LogicalInspectionIds.ToArray()},CAST({source.ReferenceFingerprint?.ToArray()} AS bytea),{_clock.GetUtcNow().UtcTicks})
                """, ct);
            try
            {
                for (int ordinal = 0; ordinal < source.Inputs.Count; ordinal++)
                    await InsertInputAsync(capture.JobId, number, ordinal, source.Inputs[ordinal], ct);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.CheckViolation && error.ConstraintName == "media_reconciliation_source_changed")
            { await transaction.RollbackAsync(CancellationToken.None); return MediaReconciliationErrors.Changed; }
        }
        var failure = await _db.Database.SqlQuery<FailureRow>($"""
            SELECT failure."JobId",failure."AttemptNumber",attempt."OutcomeCode" AS "Code" FROM romd."MediaReconciliationFailures" failure
            JOIN romd."MediaReconciliationAttempts" attempt ON attempt."JobId"=failure."JobId" AND attempt."Number"=failure."AttemptNumber"
            WHERE failure."Fingerprint"={capture.Identity.Fingerprint.ToArray()}
            """).SingleOrDefaultAsync(ct);
        await transaction.CommitAsync(ct);
        return new MediaReconciliationAttempt(number, capture, failure is null ? null : new(failure.JobId, failure.AttemptNumber, failure.Code));
    }

    private async Task InsertInputAsync(Guid jobId, int number, int ordinal, MediaReconciliationInput input, CancellationToken ct)
    {
        RomRepresentationFacts? graph = input switch { MediaReconciliationInput.Logical logical => logical.Graph, MediaReconciliationInput.Artifact artifact => artifact.Graph, _ => null };
        var logicalInput = input as MediaReconciliationInput.Logical;
        var stored = input as MediaReconciliationInput.Stored;
        int file = graph?.FileId ?? stored!.FileId;
        Sha256 hash = graph?.ArtifactSha256 ?? stored!.Identity.Sha256;
        long size = graph?.ArtifactSize ?? stored!.Identity.Size;
        var logicalIdentity = logicalInput?.Output.Identity;
        byte[]? contentHash = logicalIdentity?.Sha256.ToArray() ?? stored?.Identity.Sha256.ToArray();
        long? contentSize = logicalIdentity?.Size ?? stored?.Identity.Size;
        byte[]? sha1 = logicalIdentity?.Sha1.ToArray() ?? stored?.Identity.Sha1.ToArray(), md5 = logicalIdentity?.Md5.ToArray() ?? stored?.Identity.Md5.ToArray(), crc = logicalIdentity?.Crc32.ToArray() ?? stored?.Identity.Crc32.ToArray();
        Guid? proof = logicalInput?.VerificationId ?? (input as MediaReconciliationInput.Artifact)?.VerificationId;
        var witness = (input as MediaReconciliationInput.Artifact)?.CatalogWitness;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."MediaReconciliationInputs" ("JobId","AttemptNumber","Ordinal","Role","Path","FileId","ArtifactSha256","ArtifactSize","RepresentationId","OutputId","RomId",
                "VerificationId","ParametersSha256","ContentSha256","ContentSize","Sha1","Md5","Crc32","WitnessCatalogFileId","WitnessBindingId","WitnessRevision")
            VALUES ({jobId},{number},{ordinal},{input.Role.ToString()},CAST({input.Path} AS text),{file},{hash.ToArray()},{size},CAST({graph?.Id} AS integer),
                CAST({logicalInput?.Output.Id} AS integer),CAST({logicalInput?.Output.RomId} AS integer),CAST({proof} AS uuid),CAST({graph?.Container.Fingerprint?.ToArray()} AS bytea),
                CAST({contentHash} AS bytea),CAST({contentSize} AS bigint),CAST({sha1} AS bytea),CAST({md5} AS bytea),CAST({crc} AS bytea),
                CAST({witness?.Requirement.CatalogFileId} AS integer),CAST({witness?.BindingId} AS integer),CAST({witness?.Revision} AS uuid));
            INSERT INTO romd."MediaReconciliationPins" ("JobId","AttemptNumber","Ordinal","FileId","RepresentationId","OutputId","VerificationId")
            VALUES ({jobId},{number},{ordinal},{file},CAST({graph?.Id} AS integer),CAST({logicalInput?.Output.Id} AS integer),CAST({proof} AS uuid));
            """, ct);
    }
}
