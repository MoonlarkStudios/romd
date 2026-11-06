using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobRunnerMediaReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_UnattributedCancellation_RethrowsWithoutMutatingDemand(bool selection) =>
        await VerifyUnattributedAsync(NewJob(selection));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_HostShutdown_PreservesIntentWithoutRequestingUserCancellation(bool selection) =>
        await VerifyShutdownAsync(NewJob(selection));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_DurableCancellation_UsesCurrentSubjectOrOwnedSelectionTermination(bool selection) =>
        await VerifyUserCancellationAsync(NewJob(selection));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_HeartbeatFenceLost_PreservesIntentWithoutTerminalMutation(bool selection) =>
        await VerifyFenceLossAsync(NewJob(selection));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_SubjectRetryAgeExhausted_RequiresStoppedCustodyBeforeTerminalJournal(bool stopped)
    {
        var job = NewJob(selection: false);
        job.RecordDeferral("Previous child custody remains unresolved.", Now.AddHours(-25));
        await using var f = new Fixture(job);
        f.Execution.FinishAsync(job.Id, 1, MediaReconciliationOutcomeKind.Failed, "retry-budget-exhausted", true, Arg.Any<CancellationToken>())
            .Returns(stopped ? new MediaReconciliationJournalOutcome(job.Id, MediaReconciliationOutcomeKind.Failed, "retry-budget-exhausted", 1, Now) : MediaReconciliationErrors.Custody);
        await f.Runner.RunAsync(job.Id, "delivery", CancellationToken.None);
        await f.Executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!);
        await f.Execution.Received(1).FinishAsync(job.Id, 1, MediaReconciliationOutcomeKind.Failed, "retry-budget-exhausted", true, Arg.Any<CancellationToken>());
        job.IsTerminal.ShouldBe(stopped);
        if (stopped)
        {
            job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Failed);
            job.OutcomeCode.ShouldBe("retry-budget-exhausted");
            await f.Repository.DidNotReceiveWithAnyArgs().TryDeferClaimedAsync(default!, default, default, default);
        }
        else
        {
            job.Outcome.ShouldBeNull();
            job.CompletedAt.ShouldBeNull();
            job.FirstDeferralAt.ShouldBe(Now.AddHours(-25));
            await f.Repository.Received(1).TryDeferClaimedAsync(job, f.Fence, Arg.Is<DateTimeOffset>(due => due > Now), Arg.Any<CancellationToken>());
        }
        await f.Repository.DidNotReceiveWithAnyArgs().TryUpdateClaimedAsync(default!, default, default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Run_SelectionRetryAgeExhausted_WaitsForOriginallyOwnedChildrenBeforeTerminalJournal(int ownedPending)
    {
        var job = NewJob(selection: true);
        job.RecordDeferral("Owned child remains active.", Now.AddHours(-25));
        await using var f = new Fixture(job);
        var state = new MediaReconciliationSelectionState(new(new MediaReconciliationSubject.LogicalRom(1), 1, 1, 0, 0, ownedPending == 0 ? 1 : 0, 0, true), false);
        f.Selections.StopOwnedAsync(job.Id, Arg.Any<CancellationToken>()).Returns(state);
        f.Selections.ReadAsync(job.Id, Arg.Any<CancellationToken>()).Returns(state);
        f.Selections.FinishAsync(job.Id, MediaReconciliationOutcomeKind.Failed, "retry-budget-exhausted", Arg.Any<CancellationToken>())
            .Returns(ownedPending == 0 ? new MediaReconciliationJournalOutcome(job.Id, MediaReconciliationOutcomeKind.Failed, "retry-budget-exhausted", 1, Now) : MediaReconciliationErrors.Changed);
        await f.Runner.RunAsync(job.Id, "delivery", CancellationToken.None);
        await f.Executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!);
        await f.Selections.Received(1).StopOwnedAsync(job.Id, Arg.Any<CancellationToken>());
        job.IsTerminal.ShouldBe(ownedPending == 0);
        if (ownedPending > 0)
        {
            job.CompletedAt.ShouldBeNull();
            job.Errors.ShouldBeEmpty();
            await f.Repository.Received(1).TryDeferClaimedAsync(job, f.Fence, Arg.Is<DateTimeOffset>(due => due > Now), Arg.Any<CancellationToken>());
        }
        else
            job.PhaseEnum.ShouldBe(MediaReconciliationPhase.Failed);
        await f.Repository.DidNotReceiveWithAnyArgs().TryUpdateClaimedAsync(default!, default, default);
        await f.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_PermanentFailureAfterFenceLoss_DoesNotStopCustodyOrCheckpoint(bool selection)
    {
        var job = NewJob(selection);
        await using var f = new Fixture(job);
        f.Repository.HasExecutionOwnershipAsync(job.Id, f.Fence, Arg.Any<CancellationToken>()).Returns(false);
        f.Executor.ExecuteAsync(job, Arg.Any<JobContext>()).Returns(Task.FromException(new JobPermanentFailureException("Invalid run.")));
        await f.Runner.RunAsync(job.Id, "delivery", CancellationToken.None);
        await AssertUnchangedAsync(f);
    }

    [Fact]
    public async Task Run_SubjectNoProgressDeferralsExhausted_UsesStoppedTerminalProtocol()
    {
        var job = NewJob(selection: false);
        for (int attempt = 0; attempt < JobContext.MaxUploadDeferrals; attempt++)
            job.RecordDeferral("Decoder busy.", Now);
        await using var f = new Fixture(job);
        f.Executor.ExecuteAsync(job, Arg.Any<JobContext>()).Returns(call =>
        { call.Arg<JobContext>().RequestDeferral(Now.AddMinutes(1), "Decoder busy."); return Task.CompletedTask; });
        await f.Runner.RunAsync(job.Id, "delivery", CancellationToken.None);
        job.PhaseEnum.ShouldBe(MediaReconciliationPhase.Failed);
        job.OutcomeCode.ShouldBe("retry-budget-exhausted");
        job.Errors.ShouldBeEmpty("the family reports its exact durable outcome code; the runner must not add a competing terminal error");
        await f.Execution.Received(1).FinishAsync(job.Id, 1, MediaReconciliationOutcomeKind.Failed, "retry-budget-exhausted", true, Arg.Any<CancellationToken>());
        await f.Repository.DidNotReceiveWithAnyArgs().TryDeferClaimedAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Run_HealthySelectionBeyondSubjectDeferralCap_UsesActualExecutorAndRetainsChildOwnership()
    {
        var job = NewJob(selection: true);
        for (int attempt = 0; attempt < JobContext.MaxUploadDeferrals; attempt++)
            job.RecordDeferral("Owned child active.", Now);
        await using var f = new Fixture(job);
        var actual = new MediaReconciliationJobExecutor(f.Execution, Substitute.For<IMediaReconciliationPlanning>(), f.Selections,
            Substitute.For<IRomOriginalMediaInspection>(), Substitute.For<IRomMediaVerification>(), Substitute.For<IMediaReconciliationEncoding>(),
            Substitute.For<IMediaReconciliationAdoption>(), Substitute.For<IMediaReconciliationPublication>(), new ManualTimeProvider(Now));
        await f.CreateRunner(actual).RunAsync(job.Id, "delivery", CancellationToken.None);
        job.IsTerminal.ShouldBeFalse();
        job.DeferralCount.ShouldBe(JobContext.MaxUploadDeferrals + 1);
        await f.Selections.Received(1).RefreshAsync(job.Id, Arg.Any<CancellationToken>());
        await f.Selections.DidNotReceiveWithAnyArgs().StopOwnedAsync(default, default);
        await f.Repository.Received(1).TryDeferClaimedAsync(job, f.Fence, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_CommittedJournalNotificationFails_DoesNotCheckpointOrRedeliverTerminalOutcome()
    {
        var job = NewJob(selection: false);
        await using var f = new Fixture(job);
        f.Execution.ReadOutcomeAsync(job.Id, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationJournalOutcome(job.Id, MediaReconciliationOutcomeKind.Completed, "Canonical", 1, Now));
        f.Notifier.NotifyJobUpdatedAsync(job, Arg.Any<CancellationToken>()).Returns(_ => job.IsTerminal ? Task.FromException(new IOException("notification transport failed")) : Task.CompletedTask);
        await Should.NotThrowAsync(() => f.Runner.RunAsync(job.Id, "delivery", CancellationToken.None));
        job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        job.Errors.ShouldBeEmpty();
        await f.Repository.DidNotReceiveWithAnyArgs().TryUpdateClaimedAsync(default!, default, default);
        await f.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default, default);
    }

    private static async Task VerifyUnattributedAsync(MediaReconciliationJob job)
    {
        await using var fixture = new Fixture(job);
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        var failure = new OperationCanceledException("An unrelated operation was cancelled.", unrelated.Token);
        fixture.Executor.ExecuteAsync(job, Arg.Any<JobContext>()).Returns(Task.FromException(failure));

        OperationCanceledException? observed = null;
        try
        { await fixture.Runner.RunAsync(job.Id, "delivery", CancellationToken.None); }
        catch (OperationCanceledException error) { observed = error; }

        observed.ShouldNotBeNull();
        observed.ShouldBeSameAs(failure);
        observed.CancellationToken.ShouldBe(unrelated.Token);
        await AssertUnchangedAsync(fixture);
    }

    private static async Task VerifyShutdownAsync(MediaReconciliationJob job)
    {
        await using var fixture = new Fixture(job);
        using var shutdown = new CancellationTokenSource();
        var failure = new OperationCanceledException("Host shutdown.", shutdown.Token);
        fixture.Executor.ExecuteAsync(job, Arg.Any<JobContext>()).Returns(_ =>
        {
            shutdown.Cancel();
            return Task.FromException(failure);
        });

        OperationCanceledException? observed = null;
        try
        { await fixture.Runner.RunAsync(job.Id, "delivery", shutdown.Token); }
        catch (OperationCanceledException error) { observed = error; }

        observed.ShouldNotBeNull();
        observed.ShouldBeSameAs(failure);
        observed.CancellationToken.ShouldBe(shutdown.Token);
        await AssertUnchangedAsync(fixture);
    }

    private static async Task VerifyUserCancellationAsync(MediaReconciliationJob job)
    {
        await using var fixture = new Fixture(job);
        fixture.Execution.IsCancellationRequestedAsync(job.Id, Arg.Any<CancellationToken>()).Returns(true);
        fixture.Executor.ExecuteAsync(job, Arg.Any<JobContext>())
            .Returns(call => AwaitCancellationAsync(call.Arg<JobContext>()));

        await fixture.Runner.RunAsync(job.Id, "delivery", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        if (job.Request is MediaReconciliationRequest.SubjectWork)
        {
            job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Cancelled);
            await fixture.Execution.Received(1).FinishAsync(job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", true, Arg.Any<CancellationToken>());
            await fixture.Selections.DidNotReceiveWithAnyArgs().StopOwnedAsync(default, default);
        }
        else
        {
            job.IsTerminal.ShouldBeFalse();
            await fixture.Selections.Received(1).StopOwnedAsync(job.Id, Arg.Any<CancellationToken>());
            await fixture.Repository.Received(1).TryDeferClaimedAsync(job, fixture.Fence,
                Arg.Is<DateTimeOffset>(due => due > Now), Arg.Any<CancellationToken>());
            await fixture.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default, default);
        }
        await fixture.Repository.DidNotReceiveWithAnyArgs().TryUpdateClaimedAsync(default!, default, default);
    }

    private static async Task VerifyFenceLossAsync(MediaReconciliationJob job)
    {
        await using var fixture = new Fixture(job);
        fixture.Repository.RenewExecutionLeaseAsync(job.Id, fixture.Fence, Arg.Any<CancellationToken>()).Returns(false);
        fixture.Executor.ExecuteAsync(job, Arg.Any<JobContext>())
            .Returns(call => AwaitCancellationAsync(call.Arg<JobContext>()));

        await fixture.Runner.RunAsync(job.Id, "delivery", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await fixture.Repository.Received(1).RenewExecutionLeaseAsync(job.Id, fixture.Fence, Arg.Any<CancellationToken>());
        await AssertUnchangedAsync(fixture);
    }

    private static Task AwaitCancellationAsync(JobContext context) =>
        Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);

    private static async Task AssertUnchangedAsync(Fixture fixture)
    {
        fixture.Job.IsTerminal.ShouldBeFalse();
        fixture.Job.CompletedAt.ShouldBeNull();
        fixture.Job.Errors.ShouldBeEmpty();
        fixture.Job.DeferralCount.ShouldBe(0);
        await fixture.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default, default);
        await fixture.Selections.DidNotReceiveWithAnyArgs().StopOwnedAsync(default, default);
        await fixture.Repository.DidNotReceiveWithAnyArgs().TryDeferClaimedAsync(default!, default, default, default);
        await fixture.Repository.DidNotReceiveWithAnyArgs().TryUpdateClaimedAsync(default!, default, default);
    }

    private static MediaReconciliationJob NewJob(bool selection)
    {
        MediaReconciliationRequest request = selection
            ? new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), new(1, 0, 0, 0), 1, false)
            : new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.LogicalRom(1));
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), request, "fixture", null, Guid.NewGuid(), Now.AddDays(-2));
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        if (selection)
            job.ApplySelectionProgress(new(new MediaReconciliationSubject.LogicalRom(1), 1, 1, 0, 0, 0, 0, true));
        return job;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IRomdOptions options = Substitute.For<IRomdOptions>();
        public MediaReconciliationJob Job { get; }
        public Guid Fence { get; } = Guid.NewGuid();
        public IJobExecutionClaimRepository<MediaReconciliationJob> Repository { get; } = Substitute.For<IJobExecutionClaimRepository<MediaReconciliationJob>>();
        public IResumableJobExecutor<MediaReconciliationJob> Executor { get; } = Substitute.For<IResumableJobExecutor<MediaReconciliationJob>>();
        public IMediaReconciliationExecution Execution { get; } = Substitute.For<IMediaReconciliationExecution>();
        public IMediaReconciliationSelections Selections { get; } = Substitute.For<IMediaReconciliationSelections>();
        public IJobNotifier Notifier { get; } = Substitute.For<IJobNotifier>();
        public JobRunner<MediaReconciliationJob> Runner { get; }
        public Fixture(MediaReconciliationJob job)
        {
            Job = job;
            Repository.TryClaimExecutionAsync(job.Id, "delivery", Arg.Any<CancellationToken>()).Returns(new JobExecutionClaim<MediaReconciliationJob>(job, Fence));
            Repository.HasExecutionOwnershipAsync(job.Id, Fence, Arg.Any<CancellationToken>()).Returns(true);
            Repository.RenewExecutionLeaseAsync(job.Id, Fence, Arg.Any<CancellationToken>()).Returns(true);
            Repository.TryUpdateClaimedAsync(job, Fence, Arg.Any<CancellationToken>()).Returns(true);
            Repository.TryDeferClaimedAsync(job, Fence, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
            Execution.ReadDemandAsync(job.Id, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationDemandState(new MediaReconciliationSubject.LogicalRom(1), 1, false, false));
            Execution.FinishAsync(job.Id, Arg.Any<long>(), Arg.Any<MediaReconciliationOutcomeKind>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<ErrorOr<MediaReconciliationJournalOutcome>>(new MediaReconciliationJournalOutcome(job.Id, call.Arg<MediaReconciliationOutcomeKind>(), call.Arg<string>(), 1, Now)));
            var state = new MediaReconciliationSelectionState(new(new MediaReconciliationSubject.LogicalRom(1), 1, 1, 0, 0, 0, 0, true), false);
            Selections.ReadAsync(job.Id, Arg.Any<CancellationToken>()).Returns(state);
            Selections.RefreshAsync(job.Id, Arg.Any<CancellationToken>()).Returns(state);
            Selections.StopOwnedAsync(job.Id, Arg.Any<CancellationToken>()).Returns(state);
            Selections.FinishAsync(job.Id, Arg.Any<MediaReconciliationOutcomeKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(MediaReconciliationErrors.Changed);
            options.DataDirectory.Returns(Path.GetTempPath());
            var services = new ServiceCollection();
            services.AddSingleton<IJobRepository<MediaReconciliationJob>>(Repository);
            services.AddSingleton(Execution);
            services.AddSingleton(Selections);
            services.AddScoped<JobAuditContext>();
            services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());
            services.AddRomdJobLifecyclePolicies();
            provider = services.BuildServiceProvider();
            Runner = CreateRunner(Executor);
        }
        public JobRunner<MediaReconciliationJob> CreateRunner(IResumableJobExecutor<MediaReconciliationJob> executor) =>
            new(provider.GetRequiredService<IServiceScopeFactory>(), executor, Notifier, options, new ManualTimeProvider(Now),
                new JobExecutionClaimOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, NullLogger<JobRunner<MediaReconciliationJob>>.Instance);
        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
