using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>One fully reserved whole-file attempt, independently inspected before and after its actual encoder invocation.</summary>
public sealed class RomWholeReconciliationEncoding
{
    private readonly IAggregateTempFileFactory _staging;
    private readonly IFileStorageService _files;
    private readonly MediaProcessingRuntime _runtime;
    private readonly MediaProcessingOptions _options;
    private readonly IRomOriginalMediaIdentifier _originals;
    private readonly Func<RvzVerifier> _rvz;
    private readonly Func<ChdVerifier> _chd;
    private readonly Func<ChdArtifactEncoder> _native;
    private readonly IMediaReconciliationMediaProfile _profile;
    private readonly TimeProvider _clock;
    public RomWholeReconciliationEncoding(IAggregateTempFileFactory staging,
        IFileStorageService files,
        MediaProcessingRuntime runtime,
        MediaProcessingOptions options,
        IRomOriginalMediaIdentifier originals,
        Func<RvzVerifier> rvz,
        Func<ChdVerifier> chd,
        Func<ChdArtifactEncoder> native,
        IMediaReconciliationMediaProfile profile,
        TimeProvider clock)
    {
        this._staging = staging;
        this._files = files;
        this._runtime = runtime;
        this._options = options.ValidatedCopy();
        this._originals = originals;
        this._rvz = rvz;
        this._chd = chd;
        this._native = native;
        this._profile = profile;
        this._clock = clock;
    }

    internal async Task<ErrorOr<IMediaReconciliationEncodingLease>> AcquireAsync(MediaReconciliationAttempt attempt, MediaReconciliationPolicy policy,
        RomWholeEncodingProfile settings, CancellationToken ct)
    {
        var capture = attempt.Capture;
        var inputs = capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().ToArray();
        if (capture.Sources.Layout is not null || inputs is not [var source] || source.Role != MediaReconciliationSourceRole.Whole ||
            source.Graph.Manifest is not RomReconstructionManifest.WholeFile || capture.DiscKind != settings.DiscKind ||
            source.Graph.Container.Encoding != RomRepresentationEncoding.Identity && source.Graph.Container.Encoding != policy.Kind.StoredEncoding)
            return MediaReconciliationErrors.Measurement;
        var limits = _options.ValidatedCopy();
        var identity = source.Output.Identity;
        long maximum = settings.MaximumOutputBytes(identity.Size, limits.MaxLogicalBytes, ct);
        if (source.Graph.ArtifactSize > limits.MaxArtifactBytes || identity.Size > limits.MaxLogicalBytes || maximum <= 0 || maximum > limits.MaxArtifactBytes)
            return MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded);
        var processing = await _runtime.AcquireAsync(capture.JobId, "reconciliation-whole", ct);
        try
        {
            var aggregate = await _staging.ReserveAggregateAsync(new(capture.JobId, "reconciliation-whole", source.Graph.ArtifactSize, maximum,
                source.Graph.Container is RomContainerFacts.Chd ? identity.Size : 0), ct);
            if (aggregate.IsError)
            { processing.Dispose(); return aggregate.Errors; }
            try
            {
                if (aggregate.Value is not IMediaStagingAggregate binding)
                { await aggregate.Value.DisposeAsync(); processing.Dispose(); return RomMediaVerificationErrors.Custody; }
                processing.TrackAggregate(binding);
                return new Lease(attempt, source, policy, settings, aggregate.Value, processing, _runtime, _files, _originals, _rvz, _chd, _native, _profile, _clock);
            }
            catch { await aggregate.Value.DisposeAsync(); throw; }
        }
        catch { processing.Dispose(); throw; }
    }
    private sealed class Lease : IMediaReconciliationEncodingLease
    {
        private readonly MediaReconciliationAttempt _attempt;
        private readonly MediaReconciliationInput.Logical _source;
        private readonly MediaReconciliationPolicy _policy;
        private readonly RomWholeEncodingProfile _settings;
        private readonly ITempFileAggregate _staging;
        private readonly MediaProcessingRuntime.MediaProcessingReservation _processing;
        private readonly MediaProcessingRuntime _runtime;
        private readonly IFileStorageService _files;
        private readonly IRomOriginalMediaIdentifier _originals;
        private readonly Func<RvzVerifier> _rvz;
        private readonly Func<ChdVerifier> _chd;
        private readonly Func<ChdArtifactEncoder> _native;
        private readonly IMediaReconciliationMediaProfile _profile;
        private readonly TimeProvider _clock;
        public Lease(MediaReconciliationAttempt attempt,
            MediaReconciliationInput.Logical source,
            MediaReconciliationPolicy policy,
            RomWholeEncodingProfile settings,
            ITempFileAggregate staging,
            MediaProcessingRuntime.MediaProcessingReservation processing,
            MediaProcessingRuntime runtime,
            IFileStorageService files,
            IRomOriginalMediaIdentifier originals,
            Func<RvzVerifier> rvz,
            Func<ChdVerifier> chd,
            Func<ChdArtifactEncoder> native,
            IMediaReconciliationMediaProfile profile,
            TimeProvider clock)
        {
            this._attempt = attempt;
            this._source = source;
            this._policy = policy;
            this._settings = settings;
            this._staging = staging;
            this._processing = processing;
            this._runtime = runtime;
            this._files = files;
            this._originals = originals;
            this._rvz = rvz;
            this._chd = chd;
            this._native = native;
            this._profile = profile;
            this._clock = clock;
        }

        private int _used, _disposed; private bool _unknown, _released; private ITempFile? _input;
        public bool ProcessTreeStopped => !_unknown && _processing.ProcessesStopped;
        public async Task<ErrorOr<MediaReconciliationEncoded>> EncodeAndVerifyAsync(Func<CancellationToken, Task> beforeVerification, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (Interlocked.Exchange(ref _used, 1) != 0)
                throw new InvalidOperationException("An encoding lease invokes its encoder at most once.");
            using var active = _processing.Activate();
            bool stopped = true;
            try
            {
                await using var physical = await _files.RetrieveByIdAsync(_source.Graph.FileId, ct);
                if (physical is null)
                    return MediaReconciliationErrors.Changed;
                var materialized = await _staging.MaterializeSourceAsync(physical, _source.Graph.ArtifactSha256, _source.Graph.ArtifactSize, ct);
                if (materialized.IsError)
                    return materialized.Errors;
                _input = materialized.Value;
                if (_input.DatHashes is not { } hashes)
                    return RomMediaVerificationErrors.Mismatch;
                ct.ThrowIfCancellationRequested();
                stopped = false;
                var inspected = await InspectAsync(_input, _source.Graph.Container.Encoding, new(_input.FileSha256, hashes.Md5, hashes.Sha1, _input.FileSize), ct);
                stopped = Stopped(inspected.IsError ? inspected.Errors : []);
                if (!stopped)
                    return RomMediaVerificationErrors.Custody;
                if (inspected.IsError)
                    return inspected.Errors;
                var sourceProof = inspected.Value;
                if (!sourceProof.Representation.MatchesRetained(_source.Graph) ||
                    !sourceProof.Representation.Measurement.Decoder.Equals(_source.Graph.Container is RomContainerFacts.Identity ? _profile.OriginalInspectionDecoder : _attempt.Capture.Decoder) ||
                    !_settings.ObservesEncodingLayout(sourceProof.Inspection))
                    return RomMediaVerificationErrors.Mismatch;
                ITempFile encoderInput = _input;
                var output = _staging.Output ?? throw new InvalidDataException("Whole encoding requires its precharged output.");
                TempFileArtifactMeasurement encodedArtifact;
                ITempFile artifact;
                string recipe, build;
                RomChdEncoderBuild? nativeBuild = null;
                if (_policy.Kind.StoredEncoding == RomRepresentationEncoding.Rvz)
                {
                    ct.ThrowIfCancellationRequested();
                    stopped = false;
                    using var admission = await _rvz().AcquireAsync(ct);
                    var encoded = await admission.EncodeAsync(encoderInput, output, _source.Output.Identity.Size, _settings.DiscKind, ct);
                    stopped = Stopped(encoded.IsError ? encoded.Errors : []);
                    if (!stopped)
                        return RomMediaVerificationErrors.Custody;
                    if (encoded.IsError)
                        return encoded.Errors;
                    var receipt = encoded.Value;
                    encodedArtifact = new(Romd.Domain.Hashing.Sha256.Parse(receipt.ArtifactSha256), Romd.Domain.Hashing.Md5.Parse(receipt.ArtifactMd5),
                        Romd.Domain.Hashing.Sha1.Parse(receipt.ArtifactSha1), receipt.ArtifactBytes);
                    var sealedResult = await output.SealAsync(encodedArtifact, ct);
                    if (sealedResult.IsError)
                        return sealedResult.Errors;
                    artifact = sealedResult.Value;
                    recipe = receipt.RecipeVersion;
                    build = receipt.EncoderBuild;
                }
                else
                {
                    var encoder = _native();
                    if (_source.Graph.Container is RomContainerFacts.Chd)
                    {
                        var raw = _staging.Verification ?? throw new InvalidDataException("Stored CHD encoding requires its precharged raw extraction.");
                        ct.ThrowIfCancellationRequested();
                        stopped = false;
                        var extracted = await encoder.ExtractAsync(_input, raw, _source.Output.Identity, ct);
                        stopped = Stopped(extracted.IsError ? extracted.Errors : []);
                        if (!stopped)
                            return RomMediaVerificationErrors.Custody;
                        if (extracted.IsError)
                            return extracted.Errors;
                        encoderInput = extracted.Value;
                    }
                    ct.ThrowIfCancellationRequested();
                    stopped = false;
                    var encoded = await encoder.EncodeAsync(encoderInput, output, _source.Output.Identity.Size, ct);
                    stopped = Stopped(encoded.IsError ? encoded.Errors : []);
                    if (!stopped)
                        return RomMediaVerificationErrors.Custody;
                    if (encoded.IsError)
                        return encoded.Errors;
                    artifact = encoded.Value.Artifact;
                    encodedArtifact = encoded.Value.Measurement;
                    recipe = encoded.Value.RecipeVersion;
                    build = encoded.Value.EncoderBuild;
                    nativeBuild = encoded.Value.NativeEncoder;
                }
                if (recipe != _attempt.Capture.Identity.Recipe!.Value || build != _attempt.Capture.EncoderBuild)
                    return MediaReconciliationErrors.Measurement;
                await beforeVerification(ct);
                ct.ThrowIfCancellationRequested();
                stopped = false;
                var measured = await InspectAsync(artifact, _policy.Kind.StoredEncoding, encodedArtifact, ct);
                stopped = Stopped(measured.IsError ? measured.Errors : []);
                if (!stopped)
                    return RomMediaVerificationErrors.Custody;
                if (measured.IsError)
                    return measured.Errors;
                if (!measured.Value.Representation.Measurement.Decoder.Equals(_attempt.Capture.Decoder) ||
                    measured.Value.Representation.Measurement.Outputs is not [var measuredOutput] || measuredOutput.Identity != _source.Output.Identity ||
                    !_policy.MatchesParameters(measured.Value.Representation.Container))
                    return MediaReconciliationErrors.Measurement;
                return new MediaReconciliationEncoded.Whole(artifact, sourceProof, measured.Value, RecipeId.Parse(recipe), build, nativeBuild, _clock.GetUtcNow());
            }
            catch (MediaWorkerStoppedCancellationException) { stopped = true; throw; }
            catch (Exception) when (!stopped) { ct.ThrowIfCancellationRequested(); return RomMediaVerificationErrors.Custody; }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or OverflowException) { return MediaReconciliationErrors.Measurement; }
            finally
            {
                if (!stopped)
                {
                    _unknown = true;
                    _processing.Retain();
                    try
                    { _input?.Quarantine(); }
                    catch (Exception) { }
                    try
                    { _staging.Verification?.Quarantine(); }
                    catch (Exception) { }
                    try
                    { _staging.Output?.Quarantine(); }
                    catch (Exception) { }
                }
            }
        }
        private bool Stopped(IReadOnlyList<Error> errors) => _processing.ProcessesStopped && !errors.Any(error =>
            error.Code is "Rom.Media.Containment" or "Rom.Media.SourceCustody" || error.Code == RomMediaVerificationErrors.Custody.Code);
        private async Task<ErrorOr<RomMediaVerificationMeasurement.Whole>> InspectAsync(ITempFile artifact, RomRepresentationEncoding encoding,
            TempFileArtifactMeasurement identity, CancellationToken ct)
        {
            RomMeasuredRepresentation measured;
            RomLogicalInspection inspection;
            if (encoding == RomRepresentationEncoding.Identity)
            {
                var result = await _originals.IdentifyAsync(artifact, ct);
                if (result.IsError)
                    return result.Errors;
                measured = new(RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance, PreparedRom.WholeFile.Measure(RomContainerFacts.Identity.Instance, result.Value.Verification, null));
                inspection = result.Value.Inspection;
            }
            else if (encoding == RomRepresentationEncoding.Rvz)
            {
                var result = await _rvz().VerifyAsync(artifact, ct);
                if (result.IsError)
                    return result.Errors;
                measured = new(result.Value.Container, RomReconstructionManifest.WholeFile.Instance, PreparedRom.WholeFile.Measure(result.Value.Container, result.Value.Verification, null));
                inspection = result.Value.Inspection;
            }
            else
            {
                var result = await _chd().VerifyAsync(artifact, ct);
                if (result.IsError)
                    return result.Errors;
                measured = new(result.Value.Container, RomReconstructionManifest.WholeFile.Instance, PreparedRom.WholeFile.Measure(result.Value.Container, result.Value.Verification, result.Value.Integrity));
                inspection = result.Value.Inspection;
            }
            return new RomMediaVerificationMeasurement.Whole(measured, inspection, identity);
        }
        public async Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default)
        {
            if (_released)
                return Result.Success;
            if (!ProcessTreeStopped || (await _runtime.EnsureJobStoppedAsync(_attempt.Capture.JobId, ct)).IsError)
                return RomMediaVerificationErrors.Custody;
            _processing.Dispose();
            if (!_processing.Released)
                return RomMediaVerificationErrors.Custody;
            _released = true;
            return Result.Success;
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            { await _staging.DisposeAsync(); }
            finally { if (!ProcessTreeStopped) _processing.Retain(); _processing.Dispose(); }
        }
    }
}
