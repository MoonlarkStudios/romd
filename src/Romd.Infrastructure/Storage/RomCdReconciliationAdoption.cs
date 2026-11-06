using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Compares authentic originals with the exact retained archive using only resident decode capability.</summary>
public sealed class RomCdReconciliationAdoption : IMediaReconciliationAdoption
{
    private readonly RomMediaComposition _composition;
    private readonly IMediaHostCapabilities _capabilities;
    private readonly IMediaReconciliationMediaProfile _profile;
    private readonly LocalTempFileFactory _staging;
    private readonly IFileStorageService _files;
    private readonly RomContentReader _reader;
    private readonly IMediaWorkerSupervisor _supervisor;
    private readonly MediaProcessingRuntime _runtime;
    private readonly TimeProvider _clock;
    public RomCdReconciliationAdoption(RomMediaComposition composition, IMediaHostCapabilities capabilities,
        IMediaReconciliationMediaProfile profile, LocalTempFileFactory staging, IFileStorageService files,
        RomContentReader reader, IMediaWorkerSupervisor supervisor, MediaProcessingRuntime runtime, TimeProvider clock)
    {
        this._composition = composition;
        this._capabilities = capabilities;
        this._profile = profile;
        this._staging = staging;
        this._files = files;
        this._reader = reader;
        this._supervisor = supervisor;
        this._runtime = runtime;
        this._clock = clock;
    }
    public async Task<ErrorOr<IMediaReconciliationAdoptionLease>> AcquireAsync(MediaReconciliationAttempt attempt, CancellationToken ct = default)
    {
        var capture = attempt.Capture;
        if (capture.Identity.Operation != MediaReconciliationOperation.Adopt || capture.KindId is not { } kind ||
            capture.Sources.Layout is not MediaReconciliationLayout.Original ||
            !_composition.Reconciliation.TryGetValue(kind, out var policy) || policy.Kind.ManifestType != MediaManifestType.CdTracks ||
            capture.Identity.Recipe != policy.Recipe || capture.Identity.LimitsFingerprint != _profile.LimitsFingerprint ||
            capture.EncoderBuild is not null || capture.DiscKind is not null ||
            capture.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().SingleOrDefault(input => input.CatalogWitness is not null) is not { } retained ||
            retained.Graph.Container is not RomContainerFacts.Chd || !policy.MatchesParameters(retained.Graph.Container))
            return MediaReconciliationErrors.Changed;
        if (!_capabilities.CanDecode(kind) || _capabilities.Decoder(RomRepresentationEncoding.Chd) is not { } resident ||
            !_capabilities.Qualifies(kind, resident.Rid) || !new RomDecoderIdentity.Native(resident).Equals(capture.Decoder))
            return RomMediaVerificationErrors.Unavailable;
        var inputs = capture.Sources.Inputs.Where(input => input is MediaReconciliationInput.Logical or MediaReconciliationInput.Stored).ToArray();
        if (inputs.OfType<MediaReconciliationInput.Logical>().Count() is < 1 or > 99 ||
            inputs.OfType<MediaReconciliationInput.Logical>().Any(input => input.Role != MediaReconciliationSourceRole.Bin || input.Output.Identity.Size % 2352 != 0) ||
            inputs.Count(input => input.Role == MediaReconciliationSourceRole.Cue) != 1 ||
            inputs.Count(input => input.Role == MediaReconciliationSourceRole.Sidecar) > 16 || inputs.Length > 116)
            return MediaReconciliationErrors.Measurement;
        long Size(MediaReconciliationInput input) => input is MediaReconciliationInput.Logical logical
            ? logical.Output.Identity.Size : ((MediaReconciliationInput.Stored)input).Identity.Size;
        var processing = await _runtime.AcquireAsync(capture.JobId, "verify-chd-cd", ct);
        try
        {
            var aggregate = await _staging.ReserveCdComparisonAsync(capture.JobId, retained.Graph.ArtifactSize, inputs.Select(Size).ToArray(), ct);
            try
            {
                processing.TrackAggregate(aggregate);
                return new Lease(attempt, retained.Graph, inputs, policy, aggregate, processing, _runtime, _files, _reader, _supervisor, _clock);
            }
            catch { await aggregate.DisposeAsync(); throw; }
        }
        catch { processing.Dispose(); throw; }
    }
    private sealed class Lease(MediaReconciliationAttempt attempt, RomRepresentationFacts retained,
        IReadOnlyList<MediaReconciliationInput> inputs, MediaReconciliationPolicy policy,
        LocalTempFileFactory.CdAttemptStaging staging, MediaProcessingRuntime.MediaProcessingReservation processing,
        MediaProcessingRuntime runtime, IFileStorageService files, RomContentReader reader,
        IMediaWorkerSupervisor supervisor, TimeProvider clock) : IMediaReconciliationAdoptionLease
    {
        private ITempFile? _artifact;
        private RomCdReconciliationSources? _originals;
        private PreparedCdSources? _prepared;
        private bool _unknown, _released;
        private int _used, _disposed;
        public bool ProcessTreeStopped => !_unknown && processing.ProcessesStopped;
        public async Task<ErrorOr<MediaReconciliationAdopted>> CompareOriginalsAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (Interlocked.Exchange(ref _used, 1) != 0)
                throw new InvalidOperationException("An adoption lease compares originals at most once.");
            using var active = processing.Activate();
            bool stopped = true;
            try
            {
                await using var source = await files.RetrieveByIdAsync(retained.FileId, ct);
                if (source is null)
                    return MediaReconciliationErrors.Changed;
                _artifact = await staging.MaterializeAsync(0, source, retained.ArtifactSha256, ct);
                var originals = await RomCdReconciliationSources.MaterializeAsync(inputs, staging, 1, files, reader, ct);
                if (originals.IsError)
                    return originals.Errors;
                _originals = originals.Value;
                var workflow = new ChdCdArchiveWorkflow(staging, supervisor, clock);
                var cue = _originals.Cue;
                _prepared = await workflow.PrepareAsync(cue.Key, cue.Value, _originals.Bins, _originals.Sidecars, ct, requiredBootProfile: null);
                ct.ThrowIfCancellationRequested();
                stopped = false;
                RomCdArchiveComparison comparison;
                try
                { comparison = await workflow.VerifyExistingAsync(_artifact, _prepared, ct); }
                catch { stopped = workflow.ProcessTreeStopped && processing.ProcessesStopped; throw; }
                stopped = workflow.ProcessTreeStopped && processing.ProcessesStopped;
                if (!stopped)
                    return RomMediaVerificationErrors.Custody;
                var measured = comparison.Measured;
                if (_artifact.DatHashes is not { } hashes || !measured.Measurement.Decoder.Equals(attempt.Capture.Decoder) ||
                    measured.Container.Fingerprint != retained.Container.Fingerprint || !policy.MatchesParameters(measured.Container) ||
                    measured.Manifest is not RomReconstructionManifest.CdTracks manifest || manifest.Members.Count != inputs.Count ||
                    !attempt.Capture.Sources.MatchesOriginals(manifest) ||
                    retained.CdBootInspections.Any(observation => observation.Boot != comparison.Boot))
                    return MediaReconciliationErrors.Measurement;
                return new MediaReconciliationAdopted(_artifact,
                    new(measured, comparison.InspectorBuild, comparison.Boot, new(_artifact.FileSha256, hashes.Md5, hashes.Sha1, _artifact.FileSize)), _originals.Stored);
            }
            catch (MediaWorkerStoppedCancellationException) { stopped = true; throw; }
            catch (Exception) when (!stopped) { ct.ThrowIfCancellationRequested(); return RomMediaVerificationErrors.Custody; }
            catch (RomCdWorkerFailureException failure) { return MediaInspectionErrors.Failed(failure.Failure); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException or FormatException or OverflowException)
            { return MediaReconciliationErrors.Measurement; }
            finally
            {
                if (!stopped)
                {
                    _unknown = true;
                    processing.Retain();
                    _originals?.Quarantine();
                    try
                    { _prepared?.Quarantine(); }
                    catch (Exception) { }
                    try
                    { _artifact?.Quarantine(); }
                    catch (Exception) { }
                }
            }
        }
        public async Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default)
        {
            if (_released)
                return Result.Success;
            if (!ProcessTreeStopped || (await runtime.EnsureJobStoppedAsync(attempt.Capture.JobId, ct)).IsError)
                return RomMediaVerificationErrors.Custody;
            processing.Dispose();
            if (!processing.Released)
                return RomMediaVerificationErrors.Custody;
            _released = true;
            return Result.Success;
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            List<Exception> errors = [];
            try
            {
                try
                { if (_prepared is not null) await _prepared.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
                try
                { if (_originals is not null) await _originals.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
                try
                { if (_artifact is not null) await _artifact.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
                try
                { await staging.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
            }
            finally { if (!ProcessTreeStopped) processing.Retain(); processing.Dispose(); }
            if (errors.Count > 0)
                throw new AggregateException("Adoption cleanup failed; unresolved custody remains retained.", errors);
        }
    }
}
