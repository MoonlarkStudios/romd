using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Independent retained-media verification. It neither encodes nor publishes nor grants read admission.</summary>
public sealed class RomMediaVerification(IAggregateTempFileFactory staging, IMediaFactReader catalog,
    IFileStorageService files, MediaProcessingRuntime runtime, MediaProcessingOptions limits,
    Func<ChdVerifier> chd, Func<RvzVerifier> rvz, RomCdMediaVerification cd) : IRomMediaVerification
{
    public async Task<ErrorOr<IRomMediaVerificationLease>> AcquireAsync(Guid jobId, RomMediaVerificationSource source,
        CancellationToken ct = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Verification requires its durable job identity.", nameof(jobId));
        var facts = (await catalog.ReadAsync([source.RepresentationId], ct)).GetValueOrDefault(source.RepresentationId);
        if (facts is null || facts.FileId != source.FileId || facts.ArtifactSha256 != source.ArtifactSha256 ||
            facts.ArtifactSize != source.ArtifactSize)
            return RomMediaVerificationErrors.Mismatch;
        if (facts.Container is RomContainerFacts.Identity ||
            (facts.Container is RomContainerFacts.Chd) != (source.Decoder is RomDecoderIdentity.Native))
            return RomMediaVerificationErrors.Unavailable;
        if (facts.ArtifactSize > limits.MaxArtifactBytes || facts.Outputs.Sum(output => output.Identity.Size) > limits.MaxLogicalBytes)
            return MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded);
        if (facts.Manifest is RomReconstructionManifest.CdTracks)
            return await cd.AcquireAsync(jobId, source, facts, ct);
        var processing = await runtime.AcquireAsync(jobId, "media-verification", ct);
        try
        {
            var aggregate = await staging.ReserveAggregateAsync(new(jobId, "media-verification", source.ArtifactSize, 0), ct);
            if (aggregate.IsError)
            { processing.Dispose(); return aggregate.Errors; }
            try
            {
                if (aggregate.Value is not IMediaStagingAggregate binding)
                {
                    await aggregate.Value.DisposeAsync();
                    processing.Dispose();
                    return RomMediaVerificationErrors.Custody;
                }
                processing.TrackAggregate(binding);
                return new Lease(jobId, source, facts, aggregate.Value, processing, files, runtime, chd, rvz);
            }
            catch { await aggregate.Value.DisposeAsync(); throw; }
        }
        catch { processing.Dispose(); throw; }
    }

    private sealed class Lease(Guid jobId, RomMediaVerificationSource source, RomRepresentationFacts retained,
        ITempFileAggregate aggregate, MediaProcessingRuntime.MediaProcessingReservation processing,
        IFileStorageService files, MediaProcessingRuntime runtime, Func<ChdVerifier> chd, Func<RvzVerifier> rvz)
        : IRomMediaVerificationLease
    {
        private bool _released, _unknownCustody;
        private int _used;
        public bool ProcessTreeStopped => !_unknownCustody && processing.ProcessesStopped;
        public async Task<ErrorOr<RomMediaVerificationMeasurement>> VerifyAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _used, 1) != 0)
                throw new InvalidOperationException("Verification leases are single-use.");
            await using var input = await files.RetrieveByIdAsync(source.FileId, ct);
            if (input is null)
                return RomMediaVerificationErrors.Mismatch;
            var materialized = await aggregate.MaterializeSourceAsync(input, source.ArtifactSha256, source.ArtifactSize, ct);
            if (materialized.IsError)
                return materialized.Errors;
            var artifact = materialized.Value;
            if (artifact.DatHashes is not { } hashes)
                return RomMediaVerificationErrors.Mismatch;
            using var active = processing.Activate();
            RomMeasuredRepresentation measured;
            RomLogicalInspection inspection;
            if (retained.Container is RomContainerFacts.Chd)
            {
                var result = await chd().VerifyAsync(artifact, ct);
                if (result.IsError)
                    return Failed(result.Errors);
                var facts = result.Value;
                measured = new(facts.Container, RomReconstructionManifest.WholeFile.Instance,
                    PreparedRom.WholeFile.Measure(facts.Container, facts.Verification, facts.Integrity));
                inspection = facts.Inspection;
            }
            else
            {
                var result = await rvz().VerifyAsync(artifact, ct);
                if (result.IsError)
                    return Failed(result.Errors);
                var facts = result.Value;
                measured = new(facts.Container, RomReconstructionManifest.WholeFile.Instance,
                    PreparedRom.WholeFile.Measure(facts.Container, facts.Verification, null));
                inspection = facts.Inspection;
            }
            if (!measured.MatchesRetained(retained) || !measured.Measurement.Decoder.Equals(source.Decoder))
                return RomMediaVerificationErrors.Mismatch;
            return new RomMediaVerificationMeasurement.Whole(measured, inspection,
                new(artifact.FileSha256, hashes.Md5, hashes.Sha1, artifact.FileSize));
        }
        private ErrorOr<RomMediaVerificationMeasurement> Failed(List<Error> errors)
        {
            if (!processing.ProcessesStopped || errors.Any(error => error.Code == "Rom.Media.Containment"))
            { _unknownCustody = true; return RomMediaVerificationErrors.Custody; }
            return errors;
        }
        public async Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default)
        {
            if (_released)
                return Result.Success;
            if (!ProcessTreeStopped || (await runtime.EnsureJobStoppedAsync(jobId, ct)).IsError)
                return RomMediaVerificationErrors.Custody;
            processing.Dispose();
            if (!processing.Released)
                return RomMediaVerificationErrors.Custody;
            _released = true;
            return Result.Success;
        }
        public async ValueTask DisposeAsync()
        {
            try
            { await aggregate.DisposeAsync(); }
            finally { if (!ProcessTreeStopped) processing.Retain(); processing.Dispose(); }
        }
    }
}
