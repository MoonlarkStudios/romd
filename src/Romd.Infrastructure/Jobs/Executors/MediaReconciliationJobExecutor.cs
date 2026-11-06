using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;

namespace Romd.Infrastructure.Jobs.Executors;

/// <summary>One bounded worker flow. Durable capture precedes admission; stopped processing precedes every fact or owner mutation.</summary>
public sealed class MediaReconciliationJobExecutor : IResumableJobExecutor<MediaReconciliationJob>
{
    private readonly IMediaReconciliationExecution _execution;
    private readonly IMediaReconciliationPlanning _planning;
    private readonly IMediaReconciliationSelections _selections;
    private readonly IRomOriginalMediaInspection _originals;
    private readonly IRomMediaVerification _verification;
    private readonly IMediaReconciliationEncoding _encoding;
    private readonly IMediaReconciliationAdoption _adoption;
    private readonly IMediaReconciliationPublication _publication;
    private readonly TimeProvider _clock;
    public MediaReconciliationJobExecutor(IMediaReconciliationExecution execution, IMediaReconciliationPlanning planning, IMediaReconciliationSelections selections,
        IRomOriginalMediaInspection originals, IRomMediaVerification verification, IMediaReconciliationEncoding encoding, IMediaReconciliationAdoption adoption, IMediaReconciliationPublication publication, TimeProvider clock)
    {
        this._execution = execution;
        this._planning = planning;
        this._selections = selections;
        this._originals = originals;
        this._verification = verification;
        this._encoding = encoding;
        this._adoption = adoption;
        this._publication = publication;
        this._clock = clock;
    }
    public async Task ExecuteAsync(MediaReconciliationJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        bool processStopped = true;
        try
        {
            if (await RecoverAsync(ct))
                return;
            await context.EnsureExecutionOwnershipAsync(ct);
            if (job.Request is MediaReconciliationRequest.SelectionWork)
            { await SelectAsync(); return; }
            for (int step = 0; step < 16; step++)
            {
                if (job.IsTerminal)
                    return;
                await context.EnsureExecutionOwnershipAsync(ct);
                var demand = await _execution.ReadDemandAsync(job.Id, ct);
                if (demand is null)
                { if (!await RecoverAsync(ct)) Defer(); return; }
                if (demand.CancelRequested)
                { await FinishAsync(demand, MediaReconciliationOutcomeKind.Cancelled, "cancelled", CancellationToken.None); return; }
                await PhaseAsync(MediaReconciliationPhase.Planning, ct);
                var planned = await _planning.NextAsync(job.Id, demand, ct);
                if (planned.IsError)
                { Defer(); return; }
                if (planned.Value is MediaReconciliationDirective.Settle settle)
                {
                    var stopped = await _execution.SupersedeAttemptAsync(job.Id, "no-further-work", processStopped, ct);
                    if (stopped.IsError)
                    { Defer(); return; }
                    if (await FinishAsync(demand, settle.Outcome, settle.Code, ct) || context.DeferredUntil is not null)
                        return;
                    continue;
                }
                if (planned.Value is MediaReconciliationDirective.Unavailable)
                { Defer(); return; }
                var capture = planned.Value switch
                {
                    MediaReconciliationDirective.Inspect inspect => inspect.Capture,
                    MediaReconciliationDirective.Verify verify => verify.Capture,
                    MediaReconciliationDirective.Adopt adopt => adopt.Capture,
                    MediaReconciliationDirective.Encode encode => encode.Capture,
                    MediaReconciliationDirective.Release release => release.Capture,
                    _ => throw new InvalidOperationException("Unsupported reconciliation directive.")
                };
                var captured = await _execution.CaptureAsync(capture, ct);
                if (captured.IsError)
                { if (Changed(captured.Errors)) continue; Defer(); return; }
                var attempt = captured.Value;
                if (attempt.PriorFailure is { } previous)
                {
                    if (!await ErrorsAsync(attempt, [Error.Validation(previous.Code, "The exact unchanged operation has a retained deterministic failure.")]))
                        return;
                    continue;
                }
                bool again = planned.Value switch
                {
                    MediaReconciliationDirective.Inspect inspect when inspect.Source.Graph.Container is RomContainerFacts.Identity => await InspectAsync(attempt, inspect.Source),
                    MediaReconciliationDirective.Inspect inspect => await VerifyAsync(attempt, new(inspect.Source.Graph.Id, inspect.Source.Graph.FileId,
                        inspect.Source.Graph.ArtifactSha256, inspect.Source.Graph.ArtifactSize, capture.Decoder)),
                    MediaReconciliationDirective.Verify verify => await VerifyAsync(attempt, verify.Source),
                    MediaReconciliationDirective.Adopt => await AdoptAsync(attempt),
                    MediaReconciliationDirective.Encode => await EncodeAsync(attempt),
                    MediaReconciliationDirective.Release release => await ReleaseAsync(attempt, release),
                    _ => throw new InvalidOperationException("Unsupported captured reconciliation directive.")
                };
                if (!again || context.DeferredUntil is not null)
                    return;
            }
            Defer(); // bounded work also bounds replanning while concurrent facts or demand keep changing
        }
        catch (ResourceCapacityException) { Defer(); }
        catch (OperationCanceledException primary)
        {
            bool cancellationRequested = false;
            try
            {
                if (await RecoverAsync(CancellationToken.None))
                    return;
                cancellationRequested = await _execution.IsCancellationRequestedAsync(job.Id, CancellationToken.None);
            }
            catch (Exception secondary)
            {
                primary.Data["Romd.MediaReconciliation.CancellationRecoveryFailure"] = secondary;
            }
            if (!cancellationRequested)
                throw;
            if (!processStopped)
            { Defer(); return; }
            if (job.Request is MediaReconciliationRequest.SelectionWork)
                await StopSelectionAsync(CancellationToken.None);
            else
            {
                MediaReconciliationDemandState? demand = null;
                bool demandReadSucceeded = false;
                try
                {
                    demand = await _execution.ReadDemandAsync(job.Id, CancellationToken.None);
                    demandReadSucceeded = true;
                }
                catch (Exception secondary)
                {
                    primary.Data["Romd.MediaReconciliation.CancellationDemandReadFailure"] = secondary;
                }
                if (!demandReadSucceeded)
                    throw;
                if (demand is not null)
                    await FinishAsync(demand, MediaReconciliationOutcomeKind.Cancelled, "cancelled", CancellationToken.None);
                else
                    Defer();
            }
        }

        async Task<bool> RecoverAsync(CancellationToken token)
        {
            if (await _execution.ReadOutcomeAsync(job.Id, token) is not { } outcome)
                return false;
            if (!job.IsTerminal && job.Request is MediaReconciliationRequest.SelectionWork)
            {
                var state = await _selections.ReadAsync(job.Id, token) ?? throw new InvalidDataException("The terminal selection lost its durable progress.");
                job.ApplySelectionProgress(state.Progress);
            }
            Apply(outcome);
            return true;
        }
        void Apply(MediaReconciliationJournalOutcome outcome)
        {
            if (outcome.JobId != job.Id)
                throw new InvalidDataException("Reconciliation journal belongs to another job.");
            job.ApplyOutcome(outcome.Kind, outcome.Code, outcome.CompletedAt);
        }
        void Defer() => context.RequestDeferral(_clock.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)),
            "Reconciliation awaits current facts, media capacity, stopped custody or selected work.");
        async Task PhaseAsync(MediaReconciliationPhase phase, CancellationToken token)
        {
            await context.EnsureExecutionOwnershipAsync(token);
            job.SetPhase(phase);
            await context.CheckpointAsync(token);
        }
        async Task<bool> FinishAsync(MediaReconciliationDemandState demand, MediaReconciliationOutcomeKind kind, string code, CancellationToken token)
        {
            ErrorOr<MediaReconciliationJournalOutcome> result;
            try
            { result = await _execution.FinishAsync(job.Id, demand.Version, kind, code, processStopped, token); }
            catch (Exception primary)
            {
                try
                {
                    if (await RecoverAsync(CancellationToken.None))
                        return true;
                }
                catch (Exception secondary)
                {
                    primary.Data["Romd.MediaReconciliation.TerminalRecoveryFailure"] = secondary;
                }
                throw;
            }
            if (result.IsError)
            { if (!Changed(result.Errors)) Defer(); return false; }
            Apply(result.Value);
            return true;
        }
        async Task<ErrorOr<Success>> MutateAsync(MediaReconciliationAttempt attempt, Func<Task<ErrorOr<Success>>> mutate)
        {
            await context.EnsureExecutionOwnershipAsync(ct);
            try
            { return await mutate(); }
            catch (Exception primary)
            {
                // A lost commit response must never cause this exact physical invocation to repeat before independent journal recovery.
                try
                {
                    var recorded = await _execution.ReadAttemptOutcomeAsync(job.Id, attempt.Number, CancellationToken.None);
                    if (recorded is { Disposition: MediaReconciliationAttemptDisposition.Recorded } && recorded.JobId == job.Id &&
                        recorded.Number == attempt.Number && recorded.Fingerprint == attempt.Capture.Identity.Fingerprint)
                        return Result.Success;
                    if (await RecoverAsync(CancellationToken.None))
                        return Result.Success;
                }
                catch (Exception secondary)
                {
                    primary.Data["Romd.MediaReconciliation.MutationRecoveryFailure"] = secondary;
                }
                throw;
            }
        }
        async Task<bool> RecordedAsync(MediaReconciliationAttempt attempt, ErrorOr<Success> result)
        {
            if (result.IsError)
                return await ErrorsAsync(attempt, result.Errors);
            context.ReportProgress();
            return !job.IsTerminal;
        }
        async Task<bool> ErrorsAsync(MediaReconciliationAttempt attempt, IReadOnlyList<Error> errors)
        {
            if (Changed(errors))
                return true;
            if (!processStopped || errors.Any(error => error.Type is ErrorType.Conflict or ErrorType.Failure or ErrorType.Unexpected))
            { Defer(); return false; }
            bool transient = errors.Any(error => error.Code is "Rom.Media.Startup" or "Rom.Media.Crash" or "Rom.Media.Protocol" or
                "Rom.Media.IoFailure" or "Rom.Media.OutputLimit" or "Rom.Media.CpuDeadline" or "Rom.Media.WallDeadline" or "Temp.SourceIdentity");
            var stopped = await _execution.FailAttemptAsync(attempt, errors[0].Code, deterministic: !transient, processTreeStopped: true, ct);
            if (stopped.IsError)
            { if (Changed(stopped.Errors)) return true; Defer(); return false; }
            if (transient)
            { Defer(); return false; }
            context.ReportProgress();
            var demand = await _execution.ReadDemandAsync(job.Id, ct);
            if (demand is null)
            { if (!await RecoverAsync(ct)) Defer(); return false; }
            if (demand.Version != attempt.Capture.RequestedVersion || demand.CancelRequested)
                return true;
            return !await FinishAsync(demand, MediaReconciliationOutcomeKind.Failed, errors[0].Code, ct) && context.DeferredUntil is null;
        }
        async Task<bool> CancelBeforePublicationAsync()
        {
            if (!await _execution.IsCancellationRequestedAsync(job.Id, ct))
                return false;
            if (await _execution.ReadDemandAsync(job.Id, ct) is { } demand)
                await FinishAsync(demand, MediaReconciliationOutcomeKind.Cancelled, "cancelled", CancellationToken.None);
            else if (!await RecoverAsync(ct))
                Defer();
            return true;
        }
        async Task<bool> InspectAsync(MediaReconciliationAttempt attempt, MediaReconciliationInput.Logical source)
        {
            await PhaseAsync(MediaReconciliationPhase.Inspecting, ct);
            var acquired = await _originals.AcquireAsync(job.Id, source, (RomDecoderIdentity.Managed)attempt.Capture.Decoder, ct);
            if (acquired.IsError)
                return await ErrorsAsync(attempt, acquired.Errors);
            await using var lease = acquired.Value;
            try
            {
                processStopped = false;
                var measured = await lease.InspectAsync(ct);
                processStopped = lease.ProcessTreeStopped;
                if (!processStopped)
                { Defer(); return false; }
                var released = await lease.ReleaseProcessingAsync(ct);
                if (released.IsError)
                { Defer(); return false; }
                if (await CancelBeforePublicationAsync())
                    return false;
                if (measured.IsError)
                    return await ErrorsAsync(attempt, measured.Errors);
                await PhaseAsync(MediaReconciliationPhase.Publishing, ct);
                return await RecordedAsync(attempt, await MutateAsync(attempt, () => _execution.RecordVerificationAsync(attempt, measured.Value, processStopped, ct)));
            }
            finally { processStopped = lease.ProcessTreeStopped; }
        }
        async Task<bool> VerifyAsync(MediaReconciliationAttempt attempt, RomMediaVerificationSource source)
        {
            await PhaseAsync(MediaReconciliationPhase.Verifying, ct);
            var acquired = await _verification.AcquireAsync(job.Id, source, ct);
            if (acquired.IsError)
                return await ErrorsAsync(attempt, acquired.Errors);
            await using var lease = acquired.Value;
            try
            {
                processStopped = false;
                var measured = await lease.VerifyAsync(ct);
                processStopped = lease.ProcessTreeStopped;
                if (!processStopped)
                { Defer(); return false; }
                var released = await lease.ReleaseProcessingAsync(ct);
                if (released.IsError)
                { Defer(); return false; }
                if (await CancelBeforePublicationAsync())
                    return false;
                if (measured.IsError)
                    return await ErrorsAsync(attempt, measured.Errors);
                await PhaseAsync(MediaReconciliationPhase.Publishing, ct);
                return await RecordedAsync(attempt, await MutateAsync(attempt, () => _execution.RecordVerificationAsync(attempt, measured.Value, processStopped, ct)));
            }
            finally { processStopped = lease.ProcessTreeStopped; }
        }
        async Task<bool> EncodeAsync(MediaReconciliationAttempt attempt)
        {
            await PhaseAsync(MediaReconciliationPhase.Encoding, ct);
            var acquired = await _encoding.AcquireAsync(attempt, ct);
            if (acquired.IsError)
                return await ErrorsAsync(attempt, acquired.Errors);
            await using var lease = acquired.Value;
            try
            {
                processStopped = false;
                var encoded = await lease.EncodeAndVerifyAsync(token => PhaseAsync(MediaReconciliationPhase.Verifying, token), ct);
                processStopped = lease.ProcessTreeStopped;
                if (!processStopped)
                { Defer(); return false; }
                var released = await lease.ReleaseProcessingAsync(ct);
                if (released.IsError)
                { Defer(); return false; }
                if (await CancelBeforePublicationAsync())
                    return false;
                if (encoded.IsError)
                    return await ErrorsAsync(attempt, encoded.Errors);
                await PhaseAsync(MediaReconciliationPhase.Publishing, ct);
                return await RecordedAsync(attempt, await MutateAsync(attempt, () => _publication.PublishAsync(attempt, encoded.Value, processStopped, ct)));
            }
            finally { processStopped = lease.ProcessTreeStopped; }
        }
        async Task<bool> AdoptAsync(MediaReconciliationAttempt attempt)
        {
            await PhaseAsync(MediaReconciliationPhase.Verifying, ct);
            var acquired = await _adoption.AcquireAsync(attempt, ct);
            if (acquired.IsError)
                return await ErrorsAsync(attempt, acquired.Errors);
            await using var lease = acquired.Value;
            try
            {
                processStopped = false;
                var compared = await lease.CompareOriginalsAsync(ct);
                processStopped = lease.ProcessTreeStopped;
                if (!processStopped)
                { Defer(); return false; }
                var released = await lease.ReleaseProcessingAsync(ct);
                if (released.IsError)
                { Defer(); return false; }
                if (await CancelBeforePublicationAsync())
                    return false;
                if (compared.IsError)
                    return await ErrorsAsync(attempt, compared.Errors);
                await PhaseAsync(MediaReconciliationPhase.Publishing, ct);
                return await RecordedAsync(attempt, await MutateAsync(attempt, () => _publication.AdoptAsync(attempt, compared.Value, processStopped, ct)));
            }
            finally { processStopped = lease.ProcessTreeStopped; }
        }
        async Task<bool> ReleaseAsync(MediaReconciliationAttempt attempt, MediaReconciliationDirective.Release release)
        {
            await PhaseAsync(MediaReconciliationPhase.Releasing, ct);
            if (await CancelBeforePublicationAsync())
                return false;
            return await RecordedAsync(attempt, await MutateAsync(attempt, () => _publication.ReleaseAsync(attempt, release.RepresentationId, release.ProofId, processStopped, ct)));
        }
        async Task StopSelectionAsync(CancellationToken token)
        {
            var stopped = await _selections.StopOwnedAsync(job.Id, token);
            if (stopped.IsError)
            { Defer(); return; }
            job.ApplySelectionProgress(stopped.Value.Progress);
            var finished = await _selections.FinishAsync(job.Id, MediaReconciliationOutcomeKind.Cancelled, "cancelled", token);
            if (finished.IsError)
            { Defer(); return; }
            var current = await _selections.ReadAsync(job.Id, token) ?? throw new InvalidDataException("Selection completion lost its durable progress.");
            job.ApplySelectionProgress(current.Progress);
            Apply(finished.Value);
        }
        async Task SelectAsync()
        {
            for (int page = 0; page < 16; page++)
            {
                await context.EnsureExecutionOwnershipAsync(ct);
                var before = job.SelectionProgress!;
                var refreshed = await _selections.RefreshAsync(job.Id, ct);
                if (refreshed.IsError)
                { if (!await RecoverAsync(ct)) Defer(); return; }
                var state = refreshed.Value;
                job.ApplySelectionProgress(state.Progress);
                if (state.Progress.Completed > before.Completed || state.Progress.Failed > before.Failed)
                    context.ReportProgress();
                if (state.CancelRequested)
                { await StopSelectionAsync(ct); return; }
                if (!state.Progress.EnumerationComplete)
                {
                    var next = await _selections.AdvanceAsync(job.Id, 64, ct);
                    if (next.IsError)
                    { Defer(); return; }
                    job.ApplySelectionProgress(next.Value.Progress);
                    context.ReportProgress();
                    await context.CheckpointAsync(ct);
                    continue;
                }
                var finished = await _selections.FinishAsync(job.Id, MediaReconciliationOutcomeKind.Completed, "selected-work-complete", ct);
                if (finished.IsError)
                { Defer(); return; }
                var final = await _selections.ReadAsync(job.Id, ct) ?? throw new InvalidDataException("Selection completion lost its durable progress.");
                job.ApplySelectionProgress(final.Progress);
                Apply(finished.Value);
                return;
            }
            Defer();
        }
    }
    private static bool Changed(IReadOnlyList<Error> errors) => errors.Any(error => error.Code == MediaReconciliationErrors.Changed.Code);
}
