using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Single-use original-byte inspection with durable staging and independently certified stop custody.</summary>
public sealed class RomOriginalMediaInspection(IAggregateTempFileFactory staging, IMediaFactReader catalog, IFileStorageService files,
    MediaProcessingRuntime runtime, MediaProcessingOptions limits, IRomOriginalMediaIdentifier originals) : IRomOriginalMediaInspection
{
    public async Task<ErrorOr<IRomOriginalMediaInspectionLease>> AcquireAsync(Guid jobId, MediaReconciliationInput.Logical source,
        RomDecoderIdentity.Managed decoder, CancellationToken ct = default)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("Original inspection requires its durable job identity.", nameof(jobId));
        if (source.Role != MediaReconciliationSourceRole.Whole || source.Graph.Container is not RomContainerFacts.Identity)
            return RomMediaVerificationErrors.Unavailable;
        var graph = (await catalog.ReadAsync([source.Graph.Id], ct)).GetValueOrDefault(source.Graph.Id);
        if (graph is null || graph.FileId != source.Graph.FileId || graph.ArtifactSha256 != source.Graph.ArtifactSha256 || graph.ArtifactSize != source.Graph.ArtifactSize ||
            graph.Container is not RomContainerFacts.Identity || !graph.Outputs.Any(output => output == source.Output) || !graph.Verifications.Any(proof => proof.Id == source.VerificationId))
            return RomMediaVerificationErrors.Mismatch;
        if (graph.ArtifactSize > limits.MaxArtifactBytes || source.Output.Identity.Size > limits.MaxLogicalBytes)
            return MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded);
        var processing = await runtime.AcquireAsync(jobId, "original-inspection", ct);
        try
        {
            var aggregate = await staging.ReserveAggregateAsync(new(jobId, "original-inspection", graph.ArtifactSize, 0), ct);
            if (aggregate.IsError)
            { processing.Dispose(); return aggregate.Errors; }
            try
            {
                if (aggregate.Value is not IMediaStagingAggregate binding)
                { await aggregate.Value.DisposeAsync(); processing.Dispose(); return RomMediaVerificationErrors.Custody; }
                processing.TrackAggregate(binding);
                return new Lease(jobId, graph, decoder, aggregate.Value, processing, files, runtime, originals);
            }
            catch { await aggregate.Value.DisposeAsync(); throw; }
        }
        catch { processing.Dispose(); throw; }
    }
    private sealed class Lease(Guid jobId, RomRepresentationFacts retained, RomDecoderIdentity.Managed decoder, ITempFileAggregate aggregate,
        MediaProcessingRuntime.MediaProcessingReservation processing, IFileStorageService files, MediaProcessingRuntime runtime, IRomOriginalMediaIdentifier originals)
        : IRomOriginalMediaInspectionLease
    {
        private int _used; private bool _unknown, _released; private ITempFile? _input;
        public bool ProcessTreeStopped => !_unknown && processing.ProcessesStopped;
        public async Task<ErrorOr<RomMediaVerificationMeasurement.Whole>> InspectAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _used, 1) != 0)
                throw new InvalidOperationException("Original inspection leases are single-use.");
            using var active = processing.Activate();
            bool stopped = true;
            try
            {
                await using var input = await files.RetrieveByIdAsync(retained.FileId, ct);
                if (input is null)
                    return RomMediaVerificationErrors.Mismatch;
                var materialized = await aggregate.MaterializeSourceAsync(input, retained.ArtifactSha256, retained.ArtifactSize, ct);
                if (materialized.IsError)
                    return materialized.Errors;
                _input = materialized.Value;
                if (_input.DatHashes is not { } hashes)
                    return RomMediaVerificationErrors.Mismatch;
                ct.ThrowIfCancellationRequested();
                stopped = false;
                var result = await originals.IdentifyAsync(_input, ct);
                stopped = processing.ProcessesStopped && (!result.IsError || !result.Errors.Any(error => error.Code is "Rom.Media.Containment" or "Rom.Media.SourceCustody"));
                if (!stopped)
                    return RomMediaVerificationErrors.Custody;
                if (result.IsError)
                    return result.Errors;
                var identified = result.Value;
                var measured = new RomMeasuredRepresentation(RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance,
                    PreparedRom.WholeFile.Measure(RomContainerFacts.Identity.Instance, identified.Verification, null));
                if (!measured.MatchesRetained(retained) || !measured.Measurement.Decoder.Equals(decoder))
                    return RomMediaVerificationErrors.Mismatch;
                return new RomMediaVerificationMeasurement.Whole(measured, identified.Inspection, new(_input.FileSha256, hashes.Md5, hashes.Sha1, _input.FileSize));
            }
            catch (MediaWorkerStoppedCancellationException) { stopped = true; throw; }
            catch (Exception) when (!stopped) { ct.ThrowIfCancellationRequested(); return RomMediaVerificationErrors.Custody; }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or OverflowException) { return RomMediaVerificationErrors.Mismatch; }
            finally
            {
                if (!stopped)
                {
                    _unknown = true;
                    processing.Retain();
                    try
                    { _input?.Quarantine(); }
                    catch (Exception) { }
                }
            }
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
