using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

public sealed partial class MediaReconciliationExecution
{
    private sealed record PublicationScope(Guid TransactionId, Guid JobId, int Number, Sha256 Fingerprint, MediaReconciliationFacts Facts, IReadOnlySet<Sha256> PublicationHashes);
    private PublicationScope? _publication;
    public async Task<ErrorOr<Success>> PrepareAsync(MediaReconciliationAttempt attempt, IReadOnlyCollection<Sha256> publicationHashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(publicationHashes);
        var capture = attempt.Capture;
        RequireOwnedTransaction(capture.JobId);
        _publication = null;
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"MediaReconciliationJobs\" WHERE \"JobId\"={capture.JobId} FOR NO KEY UPDATE", ct);
        var demand = await DemandAsync(_db, capture.JobId, ct);
        if (demand is null || demand.CancelRequested || demand.Version < capture.RequestedVersion)
            return MediaReconciliationErrors.Changed;
        var active = await _db.Database.SqlQuery<AttemptRow>($"""
            SELECT "Number","RequestedVersion","Fingerprint","Outcome" FROM romd."MediaReconciliationAttempts"
            WHERE "JobId"={capture.JobId} AND "Number"={attempt.Number} AND "Outcome" IS NULL
            """).SingleOrDefaultAsync(ct);
        if (active is null || active.RequestedVersion != capture.RequestedVersion || !active.Fingerprint.AsSpan().SequenceEqual(capture.Identity.Fingerprint.AsSpan()))
            return MediaReconciliationErrors.Changed;
        ErrorOr<MediaReconciliationFacts> current;
        try
        { current = await _authority.CheckAsync(capture, demand, publicationHashes, ct); }
        catch (MediaReconciliationLockScopeChangedException) { return MediaReconciliationPublicationErrors.LockScopeChanged; }
        if (current.IsError)
            return current.Errors;
        var refreshed = await RefreshSourcesAsync(demand.Subject, capture.Sources, ct);
        if (refreshed is null || refreshed.Fingerprint != capture.Sources.Fingerprint)
            return MediaReconciliationErrors.Changed;
        _publication = new(_db.Database.CurrentTransaction!.TransactionId, capture.JobId, attempt.Number, capture.Identity.Fingerprint, current.Value, publicationHashes.ToHashSet());
        return Result.Success;
    }
    private MediaReconciliationFacts RequirePublication(MediaReconciliationAttempt attempt)
    {
        RequireOwnedTransaction(attempt.Capture.JobId);
        if (_publication is not { } prepared || prepared.TransactionId != _db.Database.CurrentTransaction!.TransactionId ||
            prepared.JobId != attempt.Capture.JobId || prepared.Number != attempt.Number || prepared.Fingerprint != attempt.Capture.Identity.Fingerprint)
            throw new InvalidOperationException("Publication requires this exact attempt's successful authority preparation in the same transaction.");
        return prepared.Facts;
    }
    public async Task<ErrorOr<Success>> StageEncodedAsync(MediaReconciliationAttempt attempt, MediaReconciliationEncoded encoded, int artifactFileId,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct = default)
    {
        var current = RequirePublication(attempt);
        var capture = attempt.Capture;
        if (!_publication!.PublicationHashes.Contains(encoded.Measured.Measurement.ArtifactSha256) || descriptors.Any(descriptor => !_publication.PublicationHashes.Contains(descriptor.Sha256)) ||
            capture.Identity.Operation != MediaReconciliationOperation.Encode || encoded.Recipe != capture.Identity.Recipe || encoded.EncoderBuild != capture.EncoderBuild ||
            !encoded.Measured.Measurement.Decoder.Equals(capture.Decoder) || !_decision.AcceptsEncoding(capture, encoded))
            return MediaReconciliationErrors.Measurement;
        var logical = capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().ToArray();
        var owners = new List<MediaOutputOwner>();
        if (encoded is MediaReconciliationEncoded.Whole whole)
        {
            if (current is not MediaReconciliationFacts.Logical || logical is not [var source] || source.Role != MediaReconciliationSourceRole.Whole ||
                !whole.SourceMeasurement.Representation.MatchesRetained(source.Graph) || whole.SourceMeasurement.Inspection.Identity != source.Output.Identity ||
                whole.Measured.Measurement.Outputs.Single().Identity != source.Output.Identity)
                return MediaReconciliationErrors.Measurement;
            var retainedSource = await RetainWholeObservationAsync(source.Graph, whole.SourceMeasurement, ct);
            if (retainedSource.IsError)
                return retainedSource.Errors;
            owners.Add(new(RomOutputSelector.Whole.Instance, source.Output.RomId, false));
        }
        else
        {
            if (current is not MediaReconciliationFacts.Layout || encoded is not MediaReconciliationEncoded.Cd cd ||
                cd.Measured.Manifest is not RomReconstructionManifest.CdTracks manifest ||
                manifest.Members.Count != capture.Sources.Inputs.Count(input => input is not MediaReconciliationInput.Artifact))
                return MediaReconciliationErrors.Measurement;
            if (!capture.Sources.MatchesOriginals(manifest))
                return MediaReconciliationErrors.Measurement;
            owners.AddRange(CapturedCdOwners(capture, manifest));
        }
        var target = await RetainTargetAsync(artifactFileId, encoded.Measured, owners, descriptors, ct);
        Guid proof = target.Verifications.Single(row => row.Decoder.Equals(capture.Decoder)).Id;
        var comparisons = logical.Select(source =>
        {
            var output = target.Outputs.Single(output => output.Selector is RomOutputSelector.Whole || output.Selector is RomOutputSelector.Member member &&
                ((RomReconstructionManifest.CdTracks)target.Manifest).Members[member.Ordinal].Path == source.Path);
            return new RomCreationSource(output.Id, source.Graph.Id, source.Output.Id, source.VerificationId, source.Graph.FileId, source.Graph.ArtifactSha256, source.Graph.ArtifactSize);
        }).ToArray();
        RomCdCreationObservation? cdObservation = null;
        if (encoded is MediaReconciliationEncoded.Cd encodedCd)
        {
            Guid boot = await new MediaCreationFactWriter(_db).StageCdBootAsync(target.Id, proof, encodedCd.Observation.InspectorBuild,
                encoded.Measured.Measurement.ObservedAt, encodedCd.Observation.Boot, ct);
            cdObservation = new(boot, encodedCd.Observation.SourceRequestSha256, encodedCd.Observation.SourceRequestBytes);
        }
        else
        {
            var observed = await RetainWholeObservationAsync(target, ((MediaReconciliationEncoded.Whole)encoded).Measurement, ct);
            if (observed.IsError)
                return observed.Errors;
        }
        var creation = new RomRepresentationCreation(encoded is MediaReconciliationEncoded.Cd actualCd ? actualCd.Observation.Id : Guid.NewGuid(), proof,
            encoded.Recipe, encoded.EncoderBuild, encoded.ComparedAt, target.Container.Fingerprint!.Value,
            encoded is MediaReconciliationEncoded.Whole actualWhole ? actualWhole.NativeEncoder : ((MediaReconciliationEncoded.Cd)encoded).Observation.Encoder, comparisons, cdObservation);
        await new MediaCreationFactWriter(_db).StageCreationAsync(target.Id, creation, ct);
        var aligned = await AlignOwnershipAsync(capture, current, target, proof, ct);
        if (aligned.IsError)
            return aligned.Errors;
        await RecordPublicationAsync(attempt, target.Id, proof, creation.Id, "encoded", ct);
        return Result.Success;
    }
    public async Task<ErrorOr<Success>> StageAdoptedAsync(MediaReconciliationAttempt attempt, MediaReconciliationAdopted adopted, int artifactFileId,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct = default)
    {
        var current = RequirePublication(attempt);
        var capture = attempt.Capture;
        var retained = capture.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().SingleOrDefault(input => input.CatalogWitness is not null)?.Graph;
        if (current is not MediaReconciliationFacts.Layout || retained is null || artifactFileId != retained.FileId ||
            !_decision.AcceptsAdoption(capture, adopted) || adopted.Measured.Manifest is not RomReconstructionManifest.CdTracks manifest ||
            !_publication!.PublicationHashes.Contains(retained.ArtifactSha256) || descriptors.Any(descriptor => !_publication.PublicationHashes.Contains(descriptor.Sha256)) ||
            manifest.Members.Count != capture.Sources.Inputs.Count(input => input is not MediaReconciliationInput.Artifact))
            return MediaReconciliationErrors.Measurement;
        if (!capture.Sources.MatchesOriginals(manifest) || capture.Sources.Inputs.OfType<MediaReconciliationInput.Stored>().Any(stored =>
            !descriptors.Any(row => row.Ordinal == Enumerable.Range(0, manifest.Members.Count).Single(index => manifest.Members[index].Path == stored.Path) &&
                row.FileId == stored.FileId && row.Sha256 == stored.Identity.Sha256 && row.Size == stored.Identity.Size)))
            return MediaReconciliationErrors.Measurement;
        var owners = CapturedCdOwners(capture, manifest);
        var target = await RetainTargetAsync(artifactFileId, adopted.Measured, owners, descriptors, ct);
        Guid proof = target.Verifications.Single(row => row.Decoder.Equals(capture.Decoder)).Id;
        if (adopted.Measurement.Boot is { } boot)
            await new MediaCreationFactWriter(_db).StageCdBootAsync(target.Id, proof, adopted.Measurement.InspectorBuild, adopted.Measured.Measurement.ObservedAt, boot, ct);
        var aligned = await AlignOwnershipAsync(capture, current, target, proof, ct);
        if (aligned.IsError)
            return aligned.Errors;
        await RecordPublicationAsync(attempt, target.Id, proof, null, "adopted", ct);
        return Result.Success;
    }
    public async Task<ErrorOr<Success>> StageReleaseAsync(MediaReconciliationAttempt attempt, int representationId, Guid verificationId, CancellationToken ct = default)
    {
        var current = RequirePublication(attempt);
        var capture = attempt.Capture;
        if (capture.Identity.Operation != MediaReconciliationOperation.Release)
            return MediaReconciliationErrors.Measurement;
        var target = (await new MediaFactReader(_db).ReadLockedPublicationAsync([representationId], ct)).GetValueOrDefault(representationId);
        bool captured = capture.Sources.Inputs.Any(input => input switch
        {
            MediaReconciliationInput.Logical source => source.Graph.Id == representationId && source.VerificationId == verificationId,
            MediaReconciliationInput.Artifact source => source.Graph.Id == representationId && source.VerificationId == verificationId,
            _ => false
        });
        if (!captured || target is null || !target.Verifications.Any(proof => proof.Id == verificationId && proof.Decoder.Equals(capture.Decoder)))
            return MediaReconciliationErrors.Measurement;
        var aligned = await AlignOwnershipAsync(capture, current, target, verificationId, ct);
        if (aligned.IsError)
            return aligned.Errors;
        await RecordPublicationAsync(attempt, target.Id, verificationId, null, "released", ct);
        return Result.Success;
    }
    private static IReadOnlyList<MediaOutputOwner> CapturedCdOwners(MediaReconciliationCapture capture, RomReconstructionManifest.CdTracks manifest)
        => capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Select(source => new MediaOutputOwner(
            new RomOutputSelector.Member(Enumerable.Range(0, manifest.Members.Count).Single(index => manifest.Members[index].Path == source.Path)), source.Output.RomId, false)).ToArray();
    private async Task<RomRepresentationFacts> RetainTargetAsync(int fileId, RomMeasuredRepresentation measured, IReadOnlyList<MediaOutputOwner> owners,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct)
    {
        int[] ids = await _db.RomRepresentations.Where(row => row.FileId == fileId).OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(ct);
        var candidates = new List<RomRepresentationFacts>();
        foreach (int[] batch in ids.Chunk(Romd.Application.Common.Content.IMediaFactReader.MaximumBatchSize))
            candidates.AddRange((await new MediaFactReader(_db).ReadLockedPublicationAsync(batch, ct)).Values);
        // One immutable row per decoder cannot acquire a different verification purpose. Keep that graph's facts intact.
        var retained = candidates.FirstOrDefault(graph => measured.MatchesRetained(graph) && graph.Descriptors.OrderBy(row => row.Ordinal).SequenceEqual(descriptors.OrderBy(row => row.Ordinal)) &&
            !graph.Verifications.Any(proof => proof.Decoder.Equals(measured.Measurement.Decoder) && proof.CheckedProfile != measured.Measurement.CheckedProfile) &&
            owners.All(owner => graph.Outputs.Any(output => output.Selector == owner.Selector && (output.RomId is null || output.RomId == owner.RomId))));
        var publisher = new MediaFactPublisher(_db);
        if (retained is null)
            return await publisher.PublishAsync(fileId, measured.Container, measured.Manifest, measured.Measurement, owners, descriptors, ct);
        foreach (var owner in owners)
        {
            int outputId = retained.Outputs.Single(output => output.Selector == owner.Selector).Id;
            await _db.RepresentationOutputs.Where(row => row.Id == outputId).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, owner.RomId), ct);
        }
        await publisher.AppendVerificationAsync(retained.Id, measured.Measurement, ct);
        return (await new MediaFactReader(_db).ReadLockedPublicationAsync([retained.Id], ct))[retained.Id];
    }
    private async Task<ErrorOr<Success>> RetainWholeObservationAsync(RomRepresentationFacts graph, RomMediaVerificationMeasurement.Whole measured, CancellationToken ct)
    {
        if (!measured.Representation.MatchesRetained(graph) || measured.Inspection.Identity != graph.Outputs.Single().Identity ||
            measured.Artifact.Sha256 != graph.ArtifactSha256 || measured.Artifact.Size != graph.ArtifactSize)
            return MediaReconciliationErrors.Measurement;
        await new FileRepository(_db).RecordArtifactChecksumsAsync(graph.FileId, measured.Artifact.Sha256, measured.Artifact.Size, measured.Artifact.Md5, measured.Artifact.Sha1, ct);
        await new MediaFactPublisher(_db).AppendVerificationAsync(graph.Id, measured.Representation.Measurement, ct);
        var retained = (await new MediaFactReader(_db).ReadLockedPublicationAsync([graph.Id], ct))[graph.Id];
        Guid proof = retained.Verifications.Single(row => row.Decoder.Equals(measured.Representation.Measurement.Decoder)).Id;
        if (retained.Outputs.Single().RomId is not { } rom)
            return MediaReconciliationErrors.Changed;
        var observed = await new LogicalInspectionFactWriter(_db).StageAsync(rom, new(graph.Id, retained.Outputs.Single().Id, graph.FileId, proof), measured.Inspection, ct);
        if (observed.IsError)
            return observed.Errors;
        if (observed.Value.Created)
        {
            var demand = await new MediaReconciliationRequests(_db, _clock).StageSubjectAsync(new(observed.Value.Id, new MediaReconciliationSubject.LogicalRom(rom), false,
                MediaReconciliationCause.Facts, null, null), ct);
            if (demand.IsError)
                return demand.Errors;
        }
        return Result.Success;
    }
    private async Task RecordPublicationAsync(MediaReconciliationAttempt attempt, int representation, Guid verification, Guid? creation, string code, CancellationToken ct)
    {
        Guid fence = Fence(attempt.Capture.JobId);
        long stopped = _clock.GetUtcNow().UtcTicks;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."MediaReconciliationAttempts" SET "ExecutionFenceToken"={fence},"Outcome"='Recorded',"OutcomeCode"={code},"Deterministic"=false,
                "ResultRepresentationId"={representation},"ResultVerificationId"={verification},"ResultCreationId"=CAST({creation} AS uuid),
                "CompletedAtUtcTicks"={stopped},"StoppedAtUtcTicks"={stopped} WHERE "JobId"={attempt.Capture.JobId} AND "Number"={attempt.Number};
            DELETE FROM romd."MediaReconciliationPins" WHERE "JobId"={attempt.Capture.JobId} AND "AttemptNumber"={attempt.Number};
            """, ct);
    }
}
