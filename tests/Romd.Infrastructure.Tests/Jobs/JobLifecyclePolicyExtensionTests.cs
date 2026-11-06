using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Lifecycle;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

/// <summary>
///     A job family unknown to the runner composes its lifecycle purely through DI registrations: no
///     runner edits, no concrete-type checks. Unregistered capabilities fall back to the defaults.
/// </summary>
public sealed class JobLifecyclePolicyExtensionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_UnregisteredFamily_UsesDefaultLifecycle()
    {
        await using var fixture = new Fixture(registerPolicies: false);
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>()).Returns(call =>
        {
            call.Arg<JobContext>().RequestDeferral(Now.AddMinutes(1), "busy");
            return Task.CompletedTask;
        });

        await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None);

        // No budget: the deferral is handed off but never counted.
        fixture.Job.DeferralCount.ShouldBe(0);
        await fixture.Repository.Received(1).TryDeferClaimedAsync(fixture.Job, fixture.Fence, Now.AddMinutes(1),
            Arg.Any<CancellationToken>());
        fixture.Ledger.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_UnregisteredFamilyUnattributedCancellation_TerminatesAsCancelled()
    {
        await using var fixture = new Fixture(registerPolicies: false);
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>())
            .Returns(Task.FromException(new OperationCanceledException()));

        await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None);

        fixture.Job.State.ShouldBe(SyntheticState.Cancelled);
        await fixture.Repository.Received(1).TryUpdateClaimedAsync(fixture.Job, fixture.Fence, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RegisteredCleanup_RunsOnlyAfterCompletionCheckpoint()
    {
        await using var fixture = new Fixture();
        fixture.Repository.TryUpdateClaimedAsync(fixture.Job, fixture.Fence, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fixture.Ledger.Record($"checkpoint:{fixture.Job.State}");
            return true;
        });

        await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None);

        fixture.Job.State.ShouldBe(SyntheticState.Completed);
        fixture.Ledger.Events.ShouldBe(["recover", "checkpoint:Completed", "cleanup:Completed"]);
    }

    [Fact]
    public async Task RunAsync_CompletionCheckpointFails_DoesNotRunCleanup()
    {
        await using var fixture = new Fixture();
        fixture.Repository.TryUpdateClaimedAsync(fixture.Job, fixture.Fence, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new IOException("database unavailable")));

        await Should.ThrowAsync<IOException>(() => fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None));

        fixture.Ledger.Events.ShouldNotContain(entry => entry.StartsWith("cleanup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_RegisteredRecovery_AppliesCommittedOutcomeWithoutCheckpoint()
    {
        await using var fixture = new Fixture();
        fixture.Ledger.CommittedOutcome = true;

        await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None);

        fixture.Job.State.ShouldBe(SyntheticState.Completed);
        fixture.Ledger.Events.ShouldBe(["recover"]);
        await fixture.Repository.DidNotReceiveWithAnyArgs().TryUpdateClaimedAsync(default!, default, default);
    }

    [Fact]
    public async Task RunAsync_RegisteredCancellationSignal_StopsThroughHeartbeatAndDefersForCustody()
    {
        await using var fixture = new Fixture();
        fixture.Ledger.CancellationRequested = true;
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>())
            .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<JobContext>().CancellationToken));

        await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        fixture.Job.IsTerminal.ShouldBeFalse();
        fixture.Job.LastDeferralError.ShouldBe("synthetic custody");
        fixture.Ledger.Events.ShouldContain("reconcile:Cancelled");
        await fixture.Repository.Received(1).TryDeferClaimedAsync(fixture.Job, fixture.Fence,
            Arg.Is<DateTimeOffset>(due => due > Now), Arg.Any<CancellationToken>());
        await fixture.Repository.DidNotReceiveWithAnyArgs().TryUpdateClaimedAsync(default!, default, default);
    }

    [Fact]
    public async Task RunAsync_RegisteredCancellationSignalNotRequested_RethrowsUnattributedCancellation()
    {
        await using var fixture = new Fixture();
        var failure = new OperationCanceledException("unrelated");
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>()).Returns(Task.FromException(failure));

        OperationCanceledException? observed = null;
        try
        { await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None); }
        catch (OperationCanceledException error) { observed = error; }

        observed.ShouldBeSameAs(failure);
        fixture.Job.IsTerminal.ShouldBeFalse();
        fixture.Ledger.Events.ShouldNotContain(entry => entry.StartsWith("reconcile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_RegisteredBudgetExhausted_FailsThroughTerminationPolicy()
    {
        await using var fixture = new Fixture();
        fixture.Job.RecordDeferral("busy", Now);
        fixture.Job.RecordDeferral("busy", Now);
        fixture.Executor.ExecuteAsync(fixture.Job, Arg.Any<JobContext>()).Returns(call =>
        {
            call.Arg<JobContext>().RequestDeferral(Now.AddMinutes(1), "still busy");
            return Task.CompletedTask;
        });

        await fixture.Runner.RunAsync(fixture.Job.Id, "delivery", CancellationToken.None);

        fixture.Job.State.ShouldBe(SyntheticState.Failed);
        fixture.Job.Errors.Single().Message.ShouldBe("Synthetic retry budget exhausted after 2 deferrals without progress: still busy");
        fixture.Ledger.Events.ShouldContain("reconcile:PermanentFailure");
        await fixture.Repository.DidNotReceiveWithAnyArgs().TryDeferClaimedAsync(default!, default, default, default);
    }

    public enum SyntheticState { Pending, Running, Completed, Failed, Cancelled }

    public sealed class SyntheticJob : CancellableJob
    {
        public SyntheticState State { get; private set; }
        public override string Phase => State.ToString();
        public override bool IsTerminal => State is SyntheticState.Completed or SyntheticState.Failed or SyntheticState.Cancelled;
        public override double ProgressPercent => IsTerminal ? 100 : 0;

        public static SyntheticJob Create() => new()
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            SourceFilename = "synthetic",
            CreatedAt = Now
        };

        public void ApplyCommittedOutcome() => State = SyntheticState.Completed;
        protected override void TransitionFromPending() => State = SyntheticState.Running;
        protected override void SetTerminalPhase(bool hasErrors) => State = SyntheticState.Completed;
        protected override void SetFailedPhase() => State = SyntheticState.Failed;
        protected override void SetCancelledPhase() => State = SyntheticState.Cancelled;
    }

    /// <summary>Stands in for a family store: durable cancellation, a committed journal and cleanup custody.</summary>
    public sealed class SyntheticLedger
    {
        private readonly List<string> _events = [];
        public bool CancellationRequested { get; set; }
        public bool CommittedOutcome { get; set; }
        public IReadOnlyList<string> Events { get { lock (_events) return [.. _events]; } }
        public void Record(string entry) { lock (_events) _events.Add(entry); }
    }

    public sealed class SyntheticLifecyclePolicy(SyntheticLedger ledger) :
        IJobCancellationPolicy<SyntheticJob>, IJobTerminationPolicy<SyntheticJob>, IJobCompletionCleanup<SyntheticJob>
    {
        public Task<JobCancellationSignal> ReadSignalAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult(ledger.CancellationRequested ? JobCancellationSignal.Requested : JobCancellationSignal.NotRequested);

        public Task<bool> TryRecoverCommittedOutcomeAsync(SyntheticJob job, CancellationToken ct = default)
        {
            ledger.Record("recover");
            if (ledger.CommittedOutcome)
                job.ApplyCommittedOutcome();
            return Task.FromResult(ledger.CommittedOutcome);
        }

        public Task<JobTermination> ReconcileAsync(SyntheticJob job, JobTerminationCause cause, CancellationToken ct = default)
        {
            ledger.Record($"reconcile:{cause}");
            return Task.FromResult(cause == JobTerminationCause.Cancelled
                ? JobTermination.Defer("synthetic custody")
                : JobTermination.Transition);
        }

        public Task RunAfterCompletionPersistedAsync(SyntheticJob job, CancellationToken ct = default)
        {
            ledger.Record($"cleanup:{job.State}");
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public SyntheticJob Job { get; } = SyntheticJob.Create();
        public Guid Fence { get; } = Guid.NewGuid();
        public SyntheticLedger Ledger { get; } = new();
        public IJobExecutionClaimRepository<SyntheticJob> Repository { get; } = Substitute.For<IJobExecutionClaimRepository<SyntheticJob>>();
        public IResumableJobExecutor<SyntheticJob> Executor { get; } = Substitute.For<IResumableJobExecutor<SyntheticJob>>();
        public JobRunner<SyntheticJob> Runner { get; }

        public Fixture(bool registerPolicies = true)
        {
            Job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            Repository.TryClaimExecutionAsync(Job.Id, "delivery", Arg.Any<CancellationToken>())
                .Returns(new JobExecutionClaim<SyntheticJob>(Job, Fence));
            Repository.HasExecutionOwnershipAsync(Job.Id, Fence, Arg.Any<CancellationToken>()).Returns(true);
            Repository.RenewExecutionLeaseAsync(Job.Id, Fence, Arg.Any<CancellationToken>()).Returns(true);
            Repository.TryUpdateClaimedAsync(Job, Fence, Arg.Any<CancellationToken>()).Returns(true);
            Repository.TryDeferClaimedAsync(Job, Fence, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(Path.GetTempPath());
            var services = new ServiceCollection();
            services.AddSingleton<IJobRepository<SyntheticJob>>(Repository);
            services.AddScoped<JobAuditContext>();
            services.AddScoped<IAuditContext>(provider => provider.GetRequiredService<JobAuditContext>());
            services.AddSingleton(Ledger);
            if (registerPolicies)
            {
                // The whole extension: registrations only.
                services.AddSingleton<IJobRetryBudgetPolicy<SyntheticJob>>(new JobRetryBudgetPolicy<SyntheticJob>(
                    new("Synthetic", TimeSpan.FromHours(1), 2)));
                services.AddScoped<SyntheticLifecyclePolicy>();
                services.AddScoped<IJobCancellationPolicy<SyntheticJob>>(sp => sp.GetRequiredService<SyntheticLifecyclePolicy>());
                services.AddScoped<IJobTerminationPolicy<SyntheticJob>>(sp => sp.GetRequiredService<SyntheticLifecyclePolicy>());
                services.AddScoped<IJobCompletionCleanup<SyntheticJob>>(sp => sp.GetRequiredService<SyntheticLifecyclePolicy>());
            }
            services.AddRomdJobLifecyclePolicies();
            _provider = services.BuildServiceProvider();
            Runner = new JobRunner<SyntheticJob>(_provider.GetRequiredService<IServiceScopeFactory>(), Executor,
                Substitute.For<IJobNotifier>(), options, new ManualTimeProvider(Now),
                new JobExecutionClaimOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(50) },
                NullLogger<JobRunner<SyntheticJob>>.Instance);
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }
}
