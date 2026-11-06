using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs;

/// <summary>
///     Shared execution machinery for every job type: workspace locking, durable claiming and fencing,
///     heartbeat scheduling, checkpoints, deferral hand-off, and notifications. Family-specific
///     behaviour is supplied per job type by lifecycle capabilities resolved from DI
///     (<see cref="IJobRetryBudgetPolicy{TJob}" />, <see cref="IJobCancellationPolicy{TJob}" />,
///     <see cref="IJobTerminationPolicy{TJob}" />, <see cref="IJobCompletionCleanup{TJob}" />);
///     the runner never names a concrete job family.
/// </summary>
public sealed class JobRunner<TJob> where TJob : Job
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobExecutor<TJob> _executor;
    private readonly IJobNotifier _notifier;
    private readonly IRomdOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly JobExecutionClaimOptions _claimOptions;
    private readonly IJobExecutionMutationGuard? _mutationGuard;
    private readonly ILogger<JobRunner<TJob>> _logger;

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        IJobExecutor<TJob> executor,
        IJobNotifier notifier,
        IRomdOptions options,
        ILogger<JobRunner<TJob>> logger)
        : this(
            scopeFactory,
            executor,
            notifier,
            options,
            TimeProvider.System,
            new JobExecutionClaimOptions(),
            null,
            logger)
    {
    }

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        IJobExecutor<TJob> executor,
        IJobNotifier notifier,
        IRomdOptions options,
        TimeProvider timeProvider,
        JobExecutionClaimOptions claimOptions,
        ILogger<JobRunner<TJob>> logger)
        : this(scopeFactory, executor, notifier, options, timeProvider, claimOptions, null, logger)
    {
    }

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        IJobExecutor<TJob> executor,
        IJobNotifier notifier,
        IRomdOptions options,
        TimeProvider timeProvider,
        JobExecutionClaimOptions claimOptions,
        IJobExecutionMutationGuard? mutationGuard,
        ILogger<JobRunner<TJob>> logger)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _notifier = notifier;
        _options = options;
        _timeProvider = timeProvider;
        _claimOptions = claimOptions;
        _mutationGuard = mutationGuard;
        _logger = logger;
    }

    public async Task RunAsync(Guid jobId, string hangfireJobId, CancellationToken ct)
    {
        // Lease expiry must not permit two attempts to extract or remove the same staged files.
        // Acquire the cross-process workspace lock before claiming; durable DB writes also use fences.
        string candidateWorkspace = Path.Combine(_options.DataDirectory, "temp", "jobs", jobId.ToString("N"));
        await using var workspaceLock = Directory.Exists(candidateWorkspace)
            ? await JobWorkspace.AcquireExecutionLockAsync(_options.DataDirectory, jobId, ct)
            : null;
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IJobRepository<TJob>>();
        // Deferral-accounting phase: only families with a durable budget count deferrals and progress.
        // Resolved before claiming so a missing lifecycle registration cannot strand a claimed job.
        var retryBudgetPolicy = scope.ServiceProvider.GetRequiredService<IJobRetryBudgetPolicy<TJob>>();

        var executionClaimRepo = repo as IJobExecutionClaimRepository<TJob>;
        var claim = executionClaimRepo is not null
            ? await executionClaimRepo
                .TryClaimExecutionAsync(jobId, hangfireJobId, ct)
            : null;
        var job = claim?.Job ?? (executionClaimRepo is null
            ? await repo.GetByIdAsync(jobId, ct)
            : null);
        if (job is null || job.IsTerminal)
            return;
        var retryBudget = retryBudgetPolicy.BudgetFor(job);
        bool wasAtomicallyClaimed = claim is not null;
        Guid? fenceToken = claim?.FenceToken;
        using var executionFence = fenceToken.HasValue
            ? JobExecutionFenceScope.Enter(jobId, fenceToken.Value, _timeProvider)
            : null;
        bool terminalPersisted = false;

        // Propagate the job creator's identity into the audit context
        if (scope.ServiceProvider.GetRequiredService<IAuditContext>() is JobAuditContext outerCtx)
            outerCtx.SetActor(job.CreatedByUserId);

        // Workspace: check if staging directory exists (created by job creators)
        var workspacePath = Path.Combine(_options.DataDirectory, "temp", "jobs", jobId.ToString("N"));
        var hasWorkspace = Directory.Exists(workspacePath);
        var workspace = hasWorkspace ? new JobWorkspace(workspacePath) : null;

        // Opt-in resume seam: executors that dispatch on the job's persisted phase are
        // started only from Pending, keep their workspace across failed attempts, and have
        // failures rethrown so the scheduler redelivers into the durable phase machine.
        bool resumable = _executor is IResumableJobExecutor<TJob>;

        async Task CheckpointAsync(CancellationToken checkpointCt)
        {
            using var innerScope = _scopeFactory.CreateScope();
            if (innerScope.ServiceProvider.GetRequiredService<IAuditContext>() is JobAuditContext innerCtx)
                innerCtx.SetActor(job.CreatedByUserId);
            var innerRepo = innerScope.ServiceProvider.GetRequiredService<IJobRepository<TJob>>();
            if (fenceToken.HasValue)
            {
                var claimedRepo = (IJobExecutionClaimRepository<TJob>)innerRepo;
                if (!await claimedRepo.TryUpdateClaimedAsync(job, fenceToken.Value, checkpointCt))
                    throw new JobExecutionOwnershipLostException();
            }
            else
            {
                var unitOfWork = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await using var transaction = await unitOfWork.BeginTransactionAsync(checkpointCt);
                await innerRepo.UpdateStagedAsync(job, checkpointCt);
                await transaction.CommitAsync(checkpointCt);
            }

            if (job.IsTerminal)
                terminalPersisted = true;
            try
            {
                await _notifier.NotifyJobUpdatedAsync(job, checkpointCt);
            }
            catch (OperationCanceledException) when (checkpointCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Failed to notify after checkpointing job {Id}", job.Id);
            }
        }

        async Task<bool> HasExecutionOwnershipAsync(CancellationToken ownershipCt)
        {
            if (!fenceToken.HasValue)
                return true;
            using var innerScope = _scopeFactory.CreateScope();
            var innerRepo = (IJobExecutionClaimRepository<TJob>)innerScope.ServiceProvider
                .GetRequiredService<IJobRepository<TJob>>();
            return await innerRepo.HasExecutionOwnershipAsync(job.Id, fenceToken.Value, ownershipCt);
        }

        async Task ExecuteOwnedMutationAsync(
            Func<CancellationToken, Task> mutation,
            CancellationToken mutationCt)
        {
            if (fenceToken.HasValue && _mutationGuard is not null)
            {
                await _mutationGuard.ExecuteAsync(job.Id, fenceToken.Value, mutation, mutationCt);
                return;
            }

            if (!await HasExecutionOwnershipAsync(mutationCt))
                throw new JobExecutionOwnershipLostException();
            await mutation(mutationCt);
        }

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeatStop = new CancellationTokenSource();
        int ownershipLost = 0;
        Task heartbeat = fenceToken.HasValue
            ? RunHeartbeatAsync(job.Id, fenceToken.Value, executionCts, heartbeatStop.Token)
            : Task.CompletedTask;

        var context = new JobContext(
            workspace?.Path,
            CheckpointAsync,
            executionCts.Token,
            HasExecutionOwnershipAsync,
            ExecuteOwnedMutationAsync, jobId: job.Id,
            onDeferral: retryBudget is null
                ? null
                : (reason, madeProgress) => job.RecordDeferral(reason, _timeProvider.GetUtcNow(), madeProgress),
            onUploadProgress: retryBudget is null ? null : job.ResetDeferralBudget);

        try
        {
            if (!wasAtomicallyClaimed)
            {
                if (!resumable || job.StartedAt is null)
                {
                    job.Start(hangfireJobId, startedAt: _timeProvider.GetUtcNow());
                }
                else
                {
                    // Redelivery of a resumable job mid-saga: rebind the scheduler id without
                    // re-running the Pending transition, and let the executor resume the phase.
                    job.SetHangfireJobId(hangfireJobId);
                }
            }

            if (wasAtomicallyClaimed)
            {
                // The conditional claim already durably persisted phase, delivery identity,
                // and lease heartbeat. Do not add a second persistence window before execution:
                // a failure there would strand a successfully claimed job before useful work.
                try
                {
                    await _notifier.NotifyJobUpdatedAsync(job, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to notify after atomically claiming job {Id}", job.Id);
                }
            }
            else
            {
                await CheckpointAsync(ct);
            }

            if (retryBudget is not null && job.FirstDeferralAt is { } firstDeferral
                && _timeProvider.GetUtcNow() - firstDeferral > retryBudget.MaximumAge)
                throw new JobPermanentFailureException(
                    $"{retryBudget.Label} retry age budget exhausted: {job.LastDeferralError ?? $"execution did not finish within {retryBudget.MaximumAge.TotalHours:0} hours"}");
            await _executor.ExecuteAsync(job, context);

            // Recovery phase: a family journal that already committed a terminal outcome wins.
            await StopHeartbeatAsync();
            if (await RecoverCommittedOutcomeAsync())
                return;
            await context.EnsureExecutionOwnershipAsync(CancellationToken.None);

            if (context.DeferredUntil is { } until)
            {
                // Completed siblings are flushed before checking the budget, so progress can
                // replenish it even when the retry request preceded an ambiguous journal flush.
                if (retryBudget?.MaximumDeferralsWithoutProgress is { } maximumDeferrals && job.DeferralCount > maximumDeferrals)
                    throw new JobPermanentFailureException(
                        $"{retryBudget.Label} retry budget exhausted after {maximumDeferrals} deferrals without progress: {job.LastDeferralError}");
                if (executionClaimRepo is null || fenceToken is null)
                    throw new InvalidOperationException("Deferral requires a durable execution claim.");
                if (!await executionClaimRepo.TryDeferClaimedAsync(job, fenceToken.Value, until, CancellationToken.None))
                    throw new JobExecutionOwnershipLostException();
                // The handoff is durable; a failed publication must not trigger redelivery of a deferred job.
                try
                {
                    await _notifier.NotifyJobUpdatedAsync(job, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to notify after deferring job {Id}", job.Id);
                }
                return;
            }

            if (!job.IsTerminal)
                DirectLifecycle().Complete(completedAt: _timeProvider.GetUtcNow());
            await CheckpointAsync(CancellationToken.None);

            // Post-commit phase: the successful checkpoint above is the durability gate. A failed
            // save falls to the catch blocks and never reaches cleanup; family backstops (such as
            // the orphaned-job sweep) finish cleanup interrupted by a crash after persistence.
            await RunCompletionCleanupAsync();
        }
        catch (JobExecutionOwnershipLostException)
        {
            _logger.LogWarning("Job {Id} stopped because execution ownership moved to another attempt", job.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Hangfire's ShutdownToken is a host-lifecycle signal, not a user cancellation.
            // Preserve the active claim and rethrow so Hangfire redelivers it. The lease fence
            // prevents this runner from checkpointing after a later attempt takes ownership.
            _logger.LogInformation("Host shutdown interrupted job {Id}; preserving claim for redelivery", job.Id);
            throw;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref ownershipLost) == 1)
        {
            _logger.LogWarning("Job {Id} stopped after its execution lease was lost", job.Id);
        }
        catch (OperationCanceledException)
        {
            // Neither host shutdown nor lease loss. A family that tracks durable cancellation rethrows
            // an unattributed cancellation unchanged so the intent is redelivered; only a recorded
            // user request (or an untracked family) reconciles and terminates the owned work.
            if (await ReadCancellationSignalAsync() == JobCancellationSignal.NotRequested)
                throw;
            await StopHeartbeatAsync();
            await TerminateAsync(JobTerminationCause.Cancelled, () => CancellableLifecycle().Cancel(_timeProvider.GetUtcNow()));
        }
        catch (JobPermanentFailureException ex)
        {
            // Permanent failures must reach the typed repository's terminal checkpoint;
            // transport failure alone cannot terminate an active durable execution claim.
            ct.ThrowIfCancellationRequested();
            await StopHeartbeatAsync();
            try
            {
                await context.EnsureExecutionOwnershipAsync(CancellationToken.None);
                await TerminateAsync(JobTerminationCause.PermanentFailure, () => DirectLifecycle().Fail(ex.Message, failedAt: _timeProvider.GetUtcNow()));
            }
            catch (JobExecutionOwnershipLostException)
            {
                _logger.LogWarning("Job {Id} could not record permanent failure because execution ownership moved", job.Id);
            }
        }
        catch (Exception) when (context.DeferredUntil is not null)
        {
            // A failed durable handoff must be redelivered, without turning throttling into
            // a terminal or attempt error. The transaction preserves the previous live claim.
            throw;
        }
        catch (Exception ex) when (resumable)
        {
            // Resumable jobs keep their persisted phase: rethrow so the scheduler redelivers
            // and the executor resumes from durable state instead of terminally failing. The
            // attempt's error is recorded first so an operator can see the cause while the
            // job is non-terminal (and after retries exhaust).
            _logger.LogError(ex, "Resumable job {Id} failed in phase {Phase}; rethrowing for redelivery", job.Id, job.Phase);
            job.RecordError("attempt", ex.Message, occurredAt: _timeProvider.GetUtcNow());
            await CheckpointAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Record a clean terminal failure with the real error. We do NOT rethrow: once a
            // job has started its workspace is disposed and the phase machine can't resume, so a
            // Hangfire retry would only re-enter a now-terminal job and surface a misleading
            // "Invalid phase transition" instead of the actual cause. The job (and its error)
            // is the source of truth the UI shows; re-run by re-uploading.
            _logger.LogError(ex, "Job {Id} failed", job.Id);
            DirectLifecycle().Fail(ex.Message, failedAt: _timeProvider.GetUtcNow());
            await CheckpointAsync(CancellationToken.None);
        }
        finally
        {
            await StopHeartbeatAsync();

            // Resumable jobs retain the workspace while non-terminal so a redelivery can
            // re-ingest; abandoned workspaces are aged out by the orphaned-job cleanup.
            if (workspace is not null && terminalPersisted)
            {
                await workspace.DisposeAsync();
            }
        }

        return;

        DirectLifecycleJob DirectLifecycle() => job as DirectLifecycleJob
            ?? throw new InvalidOperationException("This job requires its durable lifecycle outcome before terminal state can be applied.");

        CancellableJob CancellableLifecycle() => job as CancellableJob
            ?? throw new InvalidOperationException("This job does not support direct cancellation.");

        async Task<bool> RecoverCommittedOutcomeAsync()
        {
            using var recoveryScope = _scopeFactory.CreateScope();
            if (!await recoveryScope.ServiceProvider.GetRequiredService<IJobTerminationPolicy<TJob>>()
                    .TryRecoverCommittedOutcomeAsync(job, CancellationToken.None))
                return false;
            terminalPersisted = true;
            await NotifyPersistedAsync();
            return true;
        }

        async Task<JobCancellationSignal> ReadCancellationSignalAsync()
        {
            using var cancelScope = _scopeFactory.CreateScope();
            return await cancelScope.ServiceProvider.GetRequiredService<IJobCancellationPolicy<TJob>>()
                .ReadSignalAsync(job.Id, CancellationToken.None);
        }

        // Termination phase. Callers stop the heartbeat first; the policy reconciles family custody
        // under the ambient fence, then the runner publishes, defers, or checkpoints the transition.
        async Task TerminateAsync(JobTerminationCause cause, Action transition)
        {
            JobTermination termination;
            using (var terminationScope = _scopeFactory.CreateScope())
            {
                termination = await terminationScope.ServiceProvider.GetRequiredService<IJobTerminationPolicy<TJob>>()
                    .ReconcileAsync(job, cause, CancellationToken.None);
            }

            switch (termination.Kind)
            {
                case JobTerminationKind.Settled:
                    terminalPersisted = true;
                    await NotifyPersistedAsync();
                    return;
                case JobTerminationKind.Deferred:
                    await DeferForCustodyAsync(termination.DeferralReason!);
                    return;
                default:
                    transition();
                    await CheckpointAsync(CancellationToken.None);
                    return;
            }
        }

        async Task DeferForCustodyAsync(string reason)
        {
            if (executionClaimRepo is null || fenceToken is null)
                throw new InvalidOperationException("Custody deferral requires a durable execution claim.");
            job.RecordDeferral(reason, _timeProvider.GetUtcNow());
            if (!await executionClaimRepo.TryDeferClaimedAsync(job, fenceToken.Value,
                _timeProvider.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)), CancellationToken.None))
                throw new JobExecutionOwnershipLostException();
            await NotifyPersistedAsync();
        }

        async Task NotifyPersistedAsync()
        {
            // The state is already durable; a failed publication must not trigger redelivery.
            try
            { await _notifier.NotifyJobUpdatedAsync(job, CancellationToken.None); }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Failed to notify after persisting job {Id}", job.Id);
            }
        }

        async Task RunCompletionCleanupAsync()
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<IJobCompletionCleanup<TJob>>()
                    .RunAfterCompletionPersistedAsync(job, CancellationToken.None);
            }
            catch (Exception error)
            {
                // Cleanup never changes a durably completed job; its family backstop retries it.
                _logger.LogWarning(error, "Post-completion cleanup failed for job {Id}", job.Id);
            }
        }

        async Task RunHeartbeatAsync(
            Guid claimedJobId,
            Guid claimedFenceToken,
            CancellationTokenSource executionCancellation,
            CancellationToken stopToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(_claimOptions.HeartbeatInterval, _timeProvider, stopToken);
                    try
                    {
                        using var heartbeatScope = _scopeFactory.CreateScope();
                        if (await heartbeatScope.ServiceProvider.GetRequiredService<IJobCancellationPolicy<TJob>>()
                                .ReadSignalAsync(claimedJobId, stopToken) == JobCancellationSignal.Requested)
                        {
                            executionCancellation.Cancel();
                            return;
                        }
                        var heartbeatRepo = (IJobExecutionClaimRepository<TJob>)heartbeatScope.ServiceProvider
                            .GetRequiredService<IJobRepository<TJob>>();
                        if (await heartbeatRepo.RenewExecutionLeaseAsync(
                                claimedJobId,
                                claimedFenceToken,
                                stopToken))
                        {
                            continue;
                        }

                        Interlocked.Exchange(ref ownershipLost, 1);
                        executionCancellation.Cancel();
                        return;
                    }
                    catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A transient monitoring failure does not prove the fence was lost.
                        // Continue trying; mutation-time ownership checks reject an expired or
                        // superseded fence even if the executor ignores cancellation.
                        _logger.LogWarning(ex, "Failed to renew execution lease for job {Id}", claimedJobId);
                    }
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
            }
        }

        async Task StopHeartbeatAsync()
        {
            if (!heartbeatStop.IsCancellationRequested)
                heartbeatStop.Cancel();
            await heartbeat;
        }
    }
}
