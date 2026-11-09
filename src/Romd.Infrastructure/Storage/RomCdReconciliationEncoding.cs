using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Complete original CD inputs and one independently verified archive under one durable processing/staging reservation.</summary>
public sealed class RomCdReconciliationEncoding
{
    private readonly LocalTempFileFactory _staging;
    private readonly IFileStorageService _files;
    private readonly RomContentReader _reader;
    private readonly IMediaWorkerSupervisor _supervisor;
    private readonly MediaProcessingRuntime _runtime;
    private readonly ChdDeploymentRuntime _deployment;
    private readonly TimeProvider _clock;
    public RomCdReconciliationEncoding(LocalTempFileFactory staging,
        IFileStorageService files,
        RomContentReader reader,
        IMediaWorkerSupervisor supervisor,
        MediaProcessingRuntime runtime,
        ChdDeploymentRuntime deployment,
        TimeProvider clock)
    {
        this._staging = staging;
        this._files = files;
        this._reader = reader;
        this._supervisor = supervisor;
        this._runtime = runtime;
        this._deployment = deployment;
        this._clock = clock;
    }

    internal async Task<ErrorOr<IMediaReconciliationEncodingLease>> AcquireAsync(MediaReconciliationAttempt attempt, MediaReconciliationPolicy policy, RomCdStorageRecipe recipe, CancellationToken ct)
    {
        var capture = attempt.Capture;
        var inputs = capture.Sources.Inputs.Where(input => input is MediaReconciliationInput.Logical or MediaReconciliationInput.Stored).ToArray();
        if (capture.Sources.Layout is null || inputs.OfType<MediaReconciliationInput.Logical>().Any(input => input.Role != MediaReconciliationSourceRole.Bin || input.Output.Identity.Size % 2352 != 0) ||
            inputs.OfType<MediaReconciliationInput.Logical>().Count() is < 1 or > 99 || inputs.OfType<MediaReconciliationInput.Stored>().Count(input => input.Role == MediaReconciliationSourceRole.Cue) != 1 ||
            inputs.OfType<MediaReconciliationInput.Stored>().Count(input => input.Role == MediaReconciliationSourceRole.Sidecar) > 16 || inputs.Length > 116)
            return MediaReconciliationErrors.Measurement;
        if (!_deployment.IsReady || _deployment.Assets is not { Tool: { } tool })
            return RomMediaVerificationErrors.Unavailable;
        long Size(MediaReconciliationInput input) => input is MediaReconciliationInput.Logical logical ? logical.Output.Identity.Size : ((MediaReconciliationInput.Stored)input).Identity.Size;
        long frames = inputs.OfType<MediaReconciliationInput.Logical>().Sum(input => input.Output.Identity.Size / 2352);
        if (frames > 450000)
            return MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded);
        long maximum = Math.Min(ChdCdLimits.MaximumArtifactBytes, checked((frames + 99 * 3L) * 2448 + (16 << 20)));
        var processing = await _runtime.AcquireAsync(capture.JobId, "encode-chd-cd", ct);
        try
        {
            var aggregate = await _staging.ReserveCdAttemptAsync(capture.JobId, inputs.Select(Size).ToArray(), maximum, ct);
            try
            {
                processing.TrackAggregate(aggregate);
                return new Lease(attempt, inputs, policy, recipe, aggregate, processing, _runtime, _files, _reader, _supervisor, tool, _clock, maximum);
            }
            catch { await aggregate.DisposeAsync(); throw; }
        }
        catch { processing.Dispose(); throw; }
    }
    private sealed class Lease : IMediaReconciliationEncodingLease
    {
        private readonly MediaReconciliationAttempt _attempt;
        private readonly IReadOnlyList<MediaReconciliationInput> _inputs;
        private readonly MediaReconciliationPolicy _policy;
        private readonly RomCdStorageRecipe _recipe;
        private readonly LocalTempFileFactory.CdAttemptStaging _staging;
        private readonly MediaProcessingRuntime.MediaProcessingReservation _processing;
        private readonly MediaProcessingRuntime _runtime;
        private readonly IFileStorageService _files;
        private readonly RomContentReader _reader;
        private readonly IMediaWorkerSupervisor _supervisor;
        private readonly ChdmanCandidate _tool;
        private readonly TimeProvider _clock;
        private readonly long _maximum;
        public Lease(MediaReconciliationAttempt attempt,
            IReadOnlyList<MediaReconciliationInput> inputs,
            MediaReconciliationPolicy policy,
            RomCdStorageRecipe recipe,
            LocalTempFileFactory.CdAttemptStaging staging,
            MediaProcessingRuntime.MediaProcessingReservation processing,
            MediaProcessingRuntime runtime,
            IFileStorageService files,
            RomContentReader reader,
            IMediaWorkerSupervisor supervisor,
            ChdmanCandidate tool,
            TimeProvider clock,
            long maximum)
        {
            this._attempt = attempt;
            this._inputs = inputs;
            this._policy = policy;
            this._recipe = recipe;
            this._staging = staging;
            this._processing = processing;
            this._runtime = runtime;
            this._files = files;
            this._reader = reader;
            this._supervisor = supervisor;
            this._tool = tool;
            this._clock = clock;
            this._maximum = maximum;
        }

        private RomCdReconciliationSources? _originals; private PreparedCdSources? _prepared; private EncodedCdArchive? _encoded;
        private bool _unknown, _released; private int _used, _disposed;
        public bool ProcessTreeStopped => !_unknown && _processing.ProcessesStopped;
        public async Task<ErrorOr<MediaReconciliationEncoded>> EncodeAndVerifyAsync(Func<CancellationToken, Task> beforeVerification, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (Interlocked.Exchange(ref _used, 1) != 0)
                throw new InvalidOperationException("A CD encoding lease invokes its encoder at most once.");
            using var active = _processing.Activate();
            bool stopped = true;
            try
            {
                var originals = await RomCdReconciliationSources.MaterializeAsync(_inputs, _staging, 0, _files, _reader, ct);
                if (originals.IsError)
                    return originals.Errors;
                _originals = originals.Value;
                var stored = _originals.Stored;
                var workflow = new ChdCdArchiveWorkflow(_staging, _supervisor, _clock);
                var cue = _originals.Cue;
                _prepared = await workflow.PrepareAsync(cue.Key, cue.Value, _originals.Bins, _originals.Sidecars, ct, _recipe.BootProfile);
                ct.ThrowIfCancellationRequested();
                stopped = false;
                try
                { _encoded = await workflow.EncodeAsync(_prepared, _tool, _maximum, ct, beforeVerification); }
                catch
                {
                    // Exception filters run before an inner finally. Publish the actual stop observation before rethrowing to this lease's handlers.
                    stopped = workflow.ProcessTreeStopped && _processing.ProcessesStopped;
                    throw;
                }
                stopped = workflow.ProcessTreeStopped && _processing.ProcessesStopped;
                if (!stopped)
                    return RomMediaVerificationErrors.Custody;
                if (_encoded.EncodingRun.Reply?.CdEncoding is not { } receipt || receipt.ObservedBoot.Profile != _recipe.BootProfile ||
                    _encoded.EncodingRun.Reply.ToolIdentity is not { } toolIdentity ||
                    _encoded.VerificationRun.Reply?.CdVerification?.ObservedBoot != receipt.ObservedBoot || !_recipe.Matches(_encoded.Measured) ||
                    !_policy.MatchesParameters(_encoded.Measured.Container) || !_encoded.Measured.Measurement.Decoder.Equals(_attempt.Capture.Decoder))
                    return MediaReconciliationErrors.Measurement;
                if (_encoded.Measured.Manifest is not RomReconstructionManifest.CdTracks manifest || manifest.Members.Count != _inputs.Count ||
                    !_attempt.Capture.Sources.MatchesOriginals(manifest))
                    return MediaReconciliationErrors.Measurement;
                var observed = new RomCdEncodingObservation(_encoded.Measured, RecipeId.Parse(receipt.Recipe), ChdBuildEvidence.Encoder(toolIdentity),
                    Sha256.Parse(receipt.RequestSha256), receipt.RequestBytes, _encoded.VerificationRun.Reply!.Identity.DecoderBuild,
                    RomCdMeasurementMapper.MapBoot(receipt.ObservedBoot)!);
                if (observed.Recipe != _attempt.Capture.Identity.Recipe || observed.Encoder.BuildLabel != _attempt.Capture.EncoderBuild)
                    return MediaReconciliationErrors.Measurement;
                return new MediaReconciliationEncoded.Cd(_encoded.Artifact, _encoded.ArtifactMeasurement, _encoded.Measured, observed, stored, _clock.GetUtcNow());
            }
            catch (MediaWorkerStoppedCancellationException) { stopped = true; throw; }
            catch (Exception) when (!stopped) { ct.ThrowIfCancellationRequested(); return RomMediaVerificationErrors.Custody; }
            catch (RomCdWorkerFailureException failure) { return MediaInspectionErrors.Failed(failure.Failure); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException or FormatException or OverflowException) { return MediaReconciliationErrors.Measurement; }
            finally
            {
                if (!stopped)
                {
                    _unknown = true;
                    _processing.Retain();
                    _originals?.Quarantine();
                    try
                    { _prepared?.Quarantine(); }
                    catch (Exception) { }
                    try
                    { _encoded?.Artifact.Quarantine(); }
                    catch (Exception) { }
                }
            }
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
            List<Exception> errors = [];
            try
            {
                try
                { if (_encoded is not null) await _encoded.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
                try
                { if (_prepared is not null) await _prepared.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
                try
                { if (_originals is not null) await _originals.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
                try
                { await _staging.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); }
            }
            finally { if (!ProcessTreeStopped) _processing.Retain(); _processing.Dispose(); }
            if (errors.Count > 0)
                throw new AggregateException("Encoding cleanup failed; unresolved custody remains retained.", errors);
        }
    }
}
