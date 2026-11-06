using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Atomic publication from pure rank decisions. Caller owns admission and use-case changes.</summary>
public sealed class RomRepresentationPublisher
{
    private readonly IFileStorageService _files;
    private readonly IUnitOfWork _work;
    private readonly IRomRepository _roms;
    private readonly IJobItemRepository? _outcomes;
    private readonly IFileMutationLock _fileLocks;
    private readonly IMediaReconciliationRequests _requests;
    private readonly RomMediaPolicyRegistry _policies;
    private readonly RomEncodingRegistry _encodings;
    private readonly RomRepresentationWriter _writer;
    private readonly IRomPs2CurrentReferenceAuthority _ps2Authority;
    private readonly IMediaFactPublisher _facts;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _clock;

    public RomRepresentationPublisher(IFileStorageService files, IUnitOfWork work, IRomRepository roms,
        RomMediaPolicyRegistry policies, RomEncodingRegistry encodings, IFileMutationLock fileLocks, IJobItemRepository? outcomes,
        IMediaFactPublisher facts, IRomMediaObservationStore observations, IRomMediaCreationStore creations, IAuditContext audit, TimeProvider clock,
        IMediaReconciliationRequests requests, IRomPs2CurrentReferenceAuthority ps2Authority)
    {
        _files = files;
        _work = work;
        _roms = roms;
        _outcomes = outcomes;
        _fileLocks = fileLocks;
        _requests = requests;
        _policies = policies;
        _encodings = encodings;
        _writer = new(roms, work, observations, creations);
        _facts = facts;
        _audit = audit;
        _clock = clock;
        _ps2Authority = ps2Authority;
    }
    public async Task<ErrorOr<RomFile>> PublishAsync(PreparedRom.WholeFile prepared, RomFile? existing, bool canRetain,
        Func<RomFile, bool, CancellationToken, Task> apply,
        Func<RomFile, JobItem?> outcome, CancellationToken ct)
    {
        RomIsoDiscKind? sourceKind = prepared.Eligibility?.Status == RomIsoEligibilityStatus.Supported
            ? prepared.Eligibility.DiscKind : null;
        string? sourceRecipe = _policies.ResolveRecipe(prepared.Encoding, sourceKind);
        if (prepared.Encoding != RomRepresentationEncoding.Identity &&
            sourceRecipe is null)
            return RomPublicationErrors.AmbiguousProfile;
        bool requiresRaw = _encodings.Describe(prepared.Encoding).RequiresRawStorage;
        var rows = existing is null ? [] : await _roms.ListRepresentationsAsync(existing.Id, ct);
        if (existing is not null && rows.Count == 0)
            return RomPublicationErrors.RetryRequired();
        var decision = Decide(rows);
        bool retain = decision.Retention == RepresentationRetention.Retain;
        bool rawEvidence = requiresRaw && decision.Retention == RepresentationRetention.EvidenceOnly;
        await using var staged = retain || rawEvidence
            ? requiresRaw
                ? await _files.StageRawAsync(prepared.Artifact, ct)
                : await _files.StageAsync(prepared.Artifact, ct)
            : null;
        await using var transaction = await _work.BeginTransactionAsync(ct);
        try
        {
            var rom = existing;
            int? logicalId = rom?.Id;
            FileStoreResult? published = null;
            if (rom is not null)
            {
                await _roms.LockRepresentationSetAsync(rom.Id, ct);
                rom = await _roms.GetByIdAsync(rom.Id, ct);
                if (rom is null)
                {
                    await transaction.RollbackAsync(ct);
                    return RomPublicationErrors.RetryRequired();
                }
                rows = await _roms.ListRepresentationsAsync(rom.Id, ct);
                decision = Decide(rows);
                retain = decision.Retention == RepresentationRetention.Retain;
                rawEvidence = requiresRaw && decision.Retention == RepresentationRetention.EvidenceOnly;
                if ((retain || rawEvidence) && staged is null)
                {
                    await transaction.RollbackAsync(ct);
                    return RomPublicationErrors.RetryRequired();
                }
            }
            // Include the incoming artifact, current preference and every released owner. The
            // storage service reacquires the incoming transaction lock after this global order.
            foreach (var hash in rows.Where(row => row.IsPreferred || decision.Release.Contains(row))
                         .Select(row => row.ArtifactSha256).Append(prepared.Artifact.FileSha256)
                         .Distinct().OrderBy(hash => hash.ToString(), StringComparer.Ordinal))
                await _fileLocks.AcquireAsync(hash, ct);
            if (prepared.Eligibility is
                {
                    Status: RomIsoEligibilityStatus.Supported,
                    DiscKind: RomIsoDiscKind.PlayStation2Dvd
                } ps2Eligibility)
            {
                if (prepared.Ps2BootEvidence is not { } boot || !boot.MatchesMeasurement(prepared.Verification))
                {
                    await transaction.RollbackAsync(ct);
                    return RomEligibilityErrors.MeasurementNotRetained();
                }
                var currentPs2 = await _ps2Authority.ResolveAsync(prepared.Verification.Identity, boot.Boot,
                    ps2Eligibility.InspectedAt, ct);
                if (currentPs2.IsError)
                {
                    await transaction.RollbackAsync(ct);
                    return currentPs2.Errors;
                }
                if (currentPs2.Value.Status != RomPs2DvdResolutionStatus.Supported ||
                    !ps2Eligibility.MatchesObservation(currentPs2.Value.Eligibility!))
                {
                    await transaction.RollbackAsync(ct);
                    return RomEligibilityErrors.ConflictingObservation();
                }
            }
            if (staged is not null && (retain || rawEvidence))
            {
                var publication = await _files.PublishStagedAsync(staged, ct);
                if (publication.RequiresFlush)
                    await _work.FlushAsync(ct);
                var artifact = publication.ReadAfterFlush();
                if (requiresRaw &&
                    (artifact.File.Sha256 != prepared.Artifact.FileSha256 ||
                     artifact.File.Size != prepared.Artifact.FileSize || artifact.File.IsCompressed ||
                     artifact.File.SizeOnDisk != artifact.File.Size ||
                     rawEvidence && artifact.File.Id != rows.Single(row =>
                         row.ArtifactSha256 == prepared.Artifact.FileSha256).FileId))
                {
                    await transaction.RollbackAsync(ct);
                    return RomPublicationErrors.RetryRequired();
                }
                // Evidence-only publication checks the physical Raw object under the file lock.
                // It must not attach a replacement owner or rewrite a legacy representation label.
                if (retain)
                    published = artifact;
                if (rom is null)
                {
                    var logical = prepared.Measurement.Outputs.Single().Identity;
                    var now = _clock.GetUtcNow();
                    logicalId = await _facts.CreateLogicalAsync(prepared.LogicalFilename, logical, _audit.ActorId, now, ct);
                }
            }
            if (logicalId is null)
                throw new InvalidOperationException("Publication requires a logical ROM identity.");
            RomRetainedMeasurement? retainedMeasurement = null;
            if (decision.Retention != RepresentationRetention.Discard)
            {
                var measured = await _writer.RecordMeasurementAsync(logicalId.Value, prepared.Container, prepared.Measurement, prepared.Inspection, ct, published);
                if (measured.IsError)
                {
                    await transaction.RollbackAsync(ct);
                    return measured.Errors;
                }
                retainedMeasurement = measured.Value;
                var created = await _writer.RecordCreatedEvidenceAsync(logicalId.Value, measured.Value, prepared, ct);
                if (created.IsError)
                {
                    await transaction.RollbackAsync(ct);
                    return created.Errors;
                }
            }
            bool representationChanged = retain || decision.Release.Count > 0;
            if (decision.Retention != RepresentationRetention.Discard)
            {
                int preferredFileId = await _writer.PreferAndReleaseAsync(logicalId.Value, decision, rows, retainedMeasurement!, prepared, ct);
                if (rom?.FileId != preferredFileId)
                    representationChanged = true;
                rom = await _roms.GetByIdAsync(logicalId.Value, ct)
                    ?? throw new InvalidOperationException("The preferred measured ROM output is unavailable.");
            }
            if (rom is null)
                throw new InvalidOperationException("Publication did not retain a preferred logical output.");
            await apply(rom, representationChanged, ct);
            await _work.FlushAsync(ct);
            // Every admitted publication can change retained ownership even when its physical observation was deduplicated.
            // Policy, decoder readiness and source selection belong to the worker, after this transaction commits.
            Guid? actor = _audit.IsSystem ? null : _audit.ActorId;
            var accepted = await _requests.StageSubjectAsync(new(Guid.NewGuid(), new MediaReconciliationSubject.LogicalRom(rom.Id), false,
                MediaReconciliationCause.Import, actor, null), ct);
            if (accepted.IsError)
            {
                await transaction.RollbackAsync(ct);
                return accepted.Errors;
            }
            await UploadOutcomeCommit.CommitAsync(_work, transaction, _outcomes, outcome(rom), ct);
            return rom;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        RepresentationRetentionDecision Decide(IReadOnlyList<RetainedRepresentation> retained) => canRetain
            ? RepresentationRetentionPolicy.Decide(retained, prepared, _policies, sourceKind)
            : new(RepresentationRetention.Discard, retained.Single(row => row.IsPreferred).ArtifactSha256, []);
    }
}
