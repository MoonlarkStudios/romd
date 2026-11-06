using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Stages stopped measured payloads outside transactions, then atomically commits facts, provenance, ownership and the attempt journal.</summary>
public sealed class MediaReconciliationPublication : IMediaReconciliationPublication
{
    private const string StagingCleanupFailureKey = "Romd.MediaReconciliation.StagingCleanupFailure";
    private readonly IFileStorageService _files;
    private readonly IUnitOfWork _work;
    private readonly IMediaReconciliationPublicationStore _store;
    private readonly IMediaReconciliationExecution _execution;
    private readonly IMediaRunCustody _custody;
    public MediaReconciliationPublication(IFileStorageService files, IUnitOfWork work, IMediaReconciliationPublicationStore store,
        IMediaReconciliationExecution execution, IMediaRunCustody custody)
    {
        this._files = files;
        this._work = work;
        this._store = store;
        this._execution = execution;
        this._custody = custody;
    }
    public async Task<ErrorOr<Success>> PublishAsync(MediaReconciliationAttempt attempt, MediaReconciliationEncoded encoded, bool processTreeStopped, CancellationToken ct = default)
    {
        var replay = await ReplayAsync(attempt, ct);
        if (replay is { } completed)
            return completed;
        ArgumentNullException.ThrowIfNull(encoded);
        if (!processTreeStopped || (await _custody.EnsureJobStoppedAsync(attempt.Capture.JobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        if (attempt.Capture.Identity.Operation != MediaReconciliationOperation.Encode || encoded.Recipe != attempt.Capture.Identity.Recipe ||
            encoded.EncoderBuild != attempt.Capture.EncoderBuild || !encoded.Measured.Measurement.Decoder.Equals(attempt.Capture.Decoder))
            return MediaReconciliationErrors.Measurement;
        return await RetryAsync(attempt, () => PublishPayloadOnceAsync(attempt, encoded.Artifact, encoded.Measured,
            encoded.ArtifactMeasurement,
            (encoded as MediaReconciliationEncoded.Cd)?.StoredOriginals,
            (file, descriptors) => _store.StageEncodedAsync(attempt, encoded, file, descriptors, ct), ct), ct);
    }
    public async Task<ErrorOr<Success>> AdoptAsync(MediaReconciliationAttempt attempt, MediaReconciliationAdopted adopted, bool processTreeStopped, CancellationToken ct = default)
    {
        var replay = await ReplayAsync(attempt, ct);
        if (replay is { } completed)
            return completed;
        ArgumentNullException.ThrowIfNull(adopted);
        if (!processTreeStopped || (await _custody.EnsureJobStoppedAsync(attempt.Capture.JobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        if (attempt.Capture.Identity.Operation != MediaReconciliationOperation.Adopt ||
            !adopted.Measured.Measurement.Decoder.Equals(attempt.Capture.Decoder))
            return MediaReconciliationErrors.Measurement;
        return await RetryAsync(attempt, () => PublishPayloadOnceAsync(attempt, adopted.Artifact, adopted.Measured, adopted.Measurement.Artifact,
            adopted.StoredOriginals, (file, descriptors) => _store.StageAdoptedAsync(attempt, adopted, file, descriptors, ct), ct), ct);
    }
    public async Task<ErrorOr<Success>> ReleaseAsync(MediaReconciliationAttempt attempt, int representationId, Guid verificationId, bool processTreeStopped, CancellationToken ct = default)
    {
        var replay = await ReplayAsync(attempt, ct);
        if (replay is { } completed)
            return completed;
        if (!processTreeStopped || (await _custody.EnsureJobStoppedAsync(attempt.Capture.JobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        if (attempt.Capture.Identity.Operation != MediaReconciliationOperation.Release)
            return MediaReconciliationErrors.Measurement;
        return await RetryAsync(attempt, async () =>
        {
            await using var transaction = await _work.BeginTransactionAsync(ct);
            var prepared = await _store.PrepareAsync(attempt, [], ct);
            if (prepared.IsError)
                return prepared;
            var staged = await _store.StageReleaseAsync(attempt, representationId, verificationId, ct);
            if (staged.IsError)
                return staged;
            await transaction.CommitAsync(ct);
            return Result.Success;
        }, ct);
    }
    private async Task<ErrorOr<Success>> PublishPayloadOnceAsync(MediaReconciliationAttempt attempt, ITempFile artifact,
        RomMeasuredRepresentation measured, TempFileArtifactMeasurement checksums, IReadOnlyDictionary<string, ITempFile>? storedOriginals,
        Func<int, IReadOnlyList<RomRetainedDescriptor>, Task<ErrorOr<Success>>> publish, CancellationToken ct)
    {
        var stored = measured.Manifest is RomReconstructionManifest.CdTracks cd
            ? cd.Members.Select((member, ordinal) => (Member: member, Ordinal: ordinal)).Where(pair => pair.Member is RomCdStoredMember).ToArray() : [];
        List<IStagedFile> staged = [];
        Exception? failure = null;
        try
        {
            staged.Add(measured.Container is RomContainerFacts.Chd ? await _files.StageRawAsync(artifact, ct) : await _files.StageAsync(artifact, ct));
            foreach (var member in stored)
                staged.Add(await _files.StageAsync(storedOriginals![member.Member.Path], ct));
            await using var transaction = await _work.BeginTransactionAsync(ct);
            var prepared = await _store.PrepareAsync(attempt, staged.Select(file => file.Sha256).Distinct().ToArray(), ct);
            if (prepared.IsError)
                return prepared;
            var publication = await _files.PublishStagedAsync(staged, ct);
            if (publication.RequiresFlush)
                await _work.FlushAsync(ct);
            var published = publication.ReadAfterFlush();
            if (published.Count != staged.Count || published[0].File.Sha256 != artifact.FileSha256 || published[0].File.Size != artifact.FileSize ||
                measured.Container is RomContainerFacts.Chd && (published[0].File.IsCompressed || published[0].File.SizeOnDisk != published[0].File.Size))
                return MediaReconciliationErrors.Measurement;
            await _files.RecordArtifactChecksumsAsync(published[0].File.Id, checksums, ct);
            var descriptors = new List<RomRetainedDescriptor>();
            for (int index = 0; index < stored.Length; index++)
            {
                var file = published[index + 1].File;
                var member = stored[index];
                if (file.Sha256 != member.Member.Identity.Sha256 || file.Size != member.Member.Identity.Size)
                    return MediaReconciliationErrors.Measurement;
                descriptors.Add(new(member.Ordinal, file.Id, file.Sha256, file.Size));
            }
            var result = await publish(published[0].File.Id, descriptors);
            if (result.IsError)
                return result;
            await transaction.CommitAsync(ct);
            return Result.Success;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            List<Exception> cleanup = [];
            for (int index = staged.Count - 1; index >= 0; index--)
                try
                { await staged[index].DisposeAsync(); }
                catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0)
            {
                var secondary = new StagingCleanupException(cleanup);
                if (failure is null)
                    throw secondary;
                // Keep the original cancellation/fence/fault visible to the worker. Failed
                // staging cleanup still forbids a fresh publication transaction below.
                failure.Data[StagingCleanupFailureKey] = secondary;
            }
        }
    }
    private async Task<ErrorOr<Success>> RetryAsync(MediaReconciliationAttempt attempt, Func<Task<ErrorOr<Success>>> publish, CancellationToken ct)
    {
        const int maximumTransactions = 4;
        for (int number = 0; number < maximumTransactions; number++)
        {
            var replay = await ReplayAsync(attempt, ct);
            if (replay is { } completed)
                return completed;
            try
            {
                var result = await publish();
                if (!result.IsError || !result.Errors.Contains(MediaReconciliationPublicationErrors.LockScopeChanged))
                    return result;
            }
            catch (Exception error)
            {
                // A lost commit response never repeats media work or invents a second creation.
                ErrorOr<Success>? committed = null;
                bool recoveryReadSucceeded = false;
                try
                {
                    committed = await ReplayAsync(attempt, CancellationToken.None);
                    recoveryReadSucceeded = true;
                }
                catch (Exception secondary)
                {
                    error.Data["Romd.MediaReconciliation.PublicationRecoveryFailure"] = secondary;
                }
                if (!recoveryReadSucceeded)
                    throw;
                bool stagingCleanupFailed = error is StagingCleanupException || error.Data.Contains(StagingCleanupFailureKey);
                if (committed is { } outcome && !outcome.IsError && !stagingCleanupFailed)
                    return outcome;
                if (stagingCleanupFailed || _work.ConflictOf(error) != TransactionConflict.Deadlock || number == maximumTransactions - 1)
                    throw;
            }
        }
        return MediaReconciliationPublicationErrors.LockScopeChanged;
    }
    private async Task<ErrorOr<Success>?> ReplayAsync(MediaReconciliationAttempt attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (await _execution.ReadOutcomeAsync(attempt.Capture.JobId, ct) is not null)
            return Result.Success;
        var result = await _execution.ReadAttemptOutcomeAsync(attempt.Capture.JobId, attempt.Number, ct);
        if (result is null)
            return null;
        return result.Fingerprint == attempt.Capture.Identity.Fingerprint && result.Disposition == MediaReconciliationAttemptDisposition.Recorded
            ? Result.Success : MediaReconciliationErrors.Changed;
    }
    private sealed class StagingCleanupException(IEnumerable<Exception> errors) : AggregateException("Reconciliation staging cleanup failed.", errors);
}
