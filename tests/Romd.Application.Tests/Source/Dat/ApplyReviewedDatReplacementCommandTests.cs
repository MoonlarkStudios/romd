using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyReviewedDatReplacement;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class ApplyReviewedDatReplacementCommandTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 4, 12, 11, 10, TimeSpan.FromHours(3)).AddTicks(1234567);

    public static TheoryData<Error> ReviewErrors => new()
    {
        Error.NotFound("DatReview.NotFound", "The catalog no longer exists."),
        Error.NotFound("DatReview.DocumentMissing", "The original catalog document is unavailable."),
        Error.Validation("DatReview.InvalidDocument", "invalid document"),
        Error.Validation("DatReview.Identity", "wrong catalog"),
        Error.Conflict("DatReview.Stale", "changed review"),
        Error.Conflict("DatReview.Unchanged", "already active")
    };

    [Theory]
    [MemberData(nameof(ReviewErrors))]
    public async Task Handle_ReviewRejected_PreservesErrorBeforeClockLeaseCopyOrTransaction(Error error)
    {
        var fixture = new Fixture();
        fixture.Review.PrepareAsync(Arg.Any<int>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(error);
        using var candidate = new MemoryStream([9]);

        var result = await fixture.Handler.HandleAsync(new(7, candidate, "active", "candidate"));

        result.FirstError.ShouldBe(error);
        candidate.CanRead.ShouldBeTrue();
        fixture.Events.ShouldBeEmpty();
        fixture.Exclusion.ReceivedCalls().ShouldBeEmpty();
        fixture.Workspace.ReceivedCalls().ShouldBeEmpty();
        fixture.Work.ReceivedCalls().ShouldBeEmpty();
        fixture.Acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ReviewedPair_CopiesBeforeOneTransactionAndPreservesExactJobFactsAndResourceOrder()
    {
        var fixture = new Fixture();
        using var candidate = new MemoryStream([9]);
        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Handler.HandleAsync(new(7, candidate, "active", "candidate"), cancellation.Token);

        var job = fixture.Job.ShouldNotBeNull();
        result.Value.ShouldBe(job.Id);
        job.Id.ShouldNotBe(Guid.Empty);
        job.ExistingDatId.ShouldBe(7);
        job.SourceFilename.ShouldBe("reviewed-catalog.dat");
        job.PlatformId.ShouldBeNull();
        job.CreatedByUserId.ShouldBeNull();
        job.CreatedAt.ShouldBe(Now);
        job.CreatedAt.Offset.ShouldBe(Now.Offset);
        job.PhaseEnum.ShouldBe(ReplaceDatPhase.Pending);
        fixture.Events.ShouldBe(["clock", "lease", "open", "copy", "begin", "stage", "commit", "transaction-dispose", "stream-dispose", "receipt-dispose", "lease-dispose"]);
        candidate.CanRead.ShouldBeTrue();
        await fixture.Review.Received(1).PrepareAsync(7, candidate, "active", "candidate", cancellation.Token);
        await fixture.Acceptance.Received(1).AddStagedAsync(job, cancellation.Token);
        await fixture.Transaction.Received(1).CommitAsync(cancellation.Token);
        await fixture.Work.DidNotReceiveWithAnyArgs().FlushAsync(default);
        await fixture.PreparedWorkspace.DidNotReceiveWithAnyArgs().DiscardAsync(default);
        fixture.AssertAllReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CommitOrTransactionDisposalPending_KeepsLeaseAndOwnedInputsUntilSettlement(bool duringDisposal)
    {
        var fixture = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (duringDisposal)
            fixture.TransactionCleanupGate = gate.Task;
        else
            fixture.Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(gate.Task);
        using var candidate = new MemoryStream([9]);
        var operation = fixture.Handler.HandleAsync(new(7, candidate, "active", "candidate"));
        try
        {
            operation.IsCompleted.ShouldBeFalse();
            fixture.LeaseReleased.ShouldBeFalse();
            fixture.TransactionReleased.ShouldBeFalse();
            fixture.Document.DisposeCount.ShouldBe(0);
        }
        finally
        {
            gate.TrySetResult();
            await operation;
        }
        (await operation).Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.AssertAllReleased();
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("open")]
    [InlineData("copy")]
    [InlineData("begin")]
    [InlineData("stage")]
    [InlineData("commit")]
    public async Task Handle_PreparationOrAcceptanceFault_PreservesCauseAndNeverDiscardsUncertainInputs(string phase)
    {
        var fixture = new Fixture { FaultAt = phase, CleanupFaults = true };
        using var candidate = new MemoryStream([9]);

        var error = await Should.ThrowAsync<IOException>(() => fixture.Handler.HandleAsync(new(7, candidate, "active", "candidate")));

        error.ShouldBeSameAs(fixture.Fault);
        candidate.CanRead.ShouldBeTrue();
        fixture.Document.DisposeCount.ShouldBe(1);
        if (phase is "lease" or "open" or "copy")
            fixture.Work.ReceivedCalls().ShouldBeEmpty();
        if (phase is not "commit")
            await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await fixture.PreparedWorkspace.DidNotReceiveWithAnyArgs().DiscardAsync(default);
        if (phase != "lease")
            fixture.LeaseReleased.ShouldBeTrue();
        if (phase is "stage" or "commit")
            fixture.TransactionReleased.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CancelDuringCopyOrAfterStaging_PreservesTokenWithoutCommitOrInputDeletion(bool afterStage)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.AtPhase = phase =>
        {
            if (phase != (afterStage ? "stage" : "copy"))
                return;
            cancellation.Cancel();
            if (!afterStage)
                cancellation.Token.ThrowIfCancellationRequested();
        };
        fixture.CleanupFaults = true;
        using var candidate = new MemoryStream([9]);

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            fixture.Handler.HandleAsync(new(7, candidate, "active", "candidate"), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await fixture.PreparedWorkspace.DidNotReceiveWithAnyArgs().DiscardAsync(default);
        fixture.LeaseReleased.ShouldBeTrue();
        fixture.Document.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("stream")]
    [InlineData("receipt")]
    [InlineData("lease")]
    [InlineData("all")]
    public async Task Handle_CleanupFaultAfterAcknowledgedCommit_ReturnsAcceptedIdentityAndAttemptsEveryRelease(string resource)
    {
        var fixture = new Fixture { CleanupFaultAt = resource };
        using var candidate = new MemoryStream([9]);

        var result = await fixture.Handler.HandleAsync(new(7, candidate, "active", "candidate"));

        result.Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.AssertAllReleased();
        await fixture.PreparedWorkspace.DidNotReceiveWithAnyArgs().DiscardAsync(default);
    }

    [Fact]
    public async Task Handle_CancellationAfterAcknowledgedCommit_ReturnsAcceptedAndStillReleasesEveryOwnedResource()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture { AtPhase = phase => { if (phase == "commit") cancellation.Cancel(); } };
        using var candidate = new MemoryStream([9]);

        var result = await fixture.Handler.HandleAsync(new(7, candidate, "active", "candidate"), cancellation.Token);

        result.Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.AssertAllReleased();
        await fixture.PreparedWorkspace.DidNotReceiveWithAnyArgs().DiscardAsync(default);
    }

    [Fact]
    public async Task Handle_SeparateReviewedRequests_KeepDistinctJobsRatherThanInventingDeduplication()
    {
        var first = new Fixture();
        var second = new Fixture();
        using var candidate = new MemoryStream([9]);

        var one = await first.Handler.HandleAsync(new(7, candidate, "active", "candidate"));
        var two = await second.Handler.HandleAsync(new(7, candidate, "active", "candidate"));

        one.Value.ShouldNotBe(two.Value);
        first.Job.ShouldNotBeNull().CorrelationId.ShouldNotBe(second.Job.ShouldNotBeNull().CorrelationId);
        first.AssertAllReleased();
        second.AssertAllReleased();
    }

    private sealed class Fixture
    {
        public IReviewedDatReplacementPreparation Review { get; } = Substitute.For<IReviewedDatReplacementPreparation>();
        public IJobWorkspaceExclusion Exclusion { get; } = Substitute.For<IJobWorkspaceExclusion>();
        public IReplaceDatWorkspacePreparation Workspace { get; } = Substitute.For<IReplaceDatWorkspacePreparation>();
        public IPreparedJobWorkspace PreparedWorkspace { get; } = Substitute.For<IPreparedJobWorkspace>();
        public IReplaceDatJobAcceptance Acceptance { get; } = Substitute.For<IReplaceDatJobAcceptance>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        private IJobWorkspaceLease Lease { get; } = Substitute.For<IJobWorkspaceLease>();
        public List<string> Events { get; } = [];
        public IOException Fault { get; } = new("primary operation failure");
        public string? FaultAt { get; init; }
        public bool CleanupFaults { get; set; }
        public string? CleanupFaultAt { get; init; }
        public Action<string>? AtPhase { get; set; }
        public Task? TransactionCleanupGate { get; set; }
        public TrackedDocument Document { get; }
        public ReplaceDatJob? Job { get; private set; }
        public bool LeaseReleased { get; private set; }
        public bool TransactionReleased { get; private set; }
        public ApplyReviewedDatReplacementCommandHandler Handler { get; }
        private bool _copied;
        private bool _inside;
        private Guid _leaseJobId;

        public Fixture()
        {
            Document = new(this);
            Review.PrepareAsync(Arg.Any<int>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Document);
            Lease.JobId.Returns(_ => _leaseJobId);
            Exclusion.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("lease");
                _leaseJobId = call.Arg<Guid>();
                return Lease;
            });
            Workspace.PrepareAsync(Arg.Any<JobWorkspaceInput>(), Arg.Any<Stream>(), Lease, Arg.Any<CancellationToken>()).Returns(call =>
            {
                _inside.ShouldBeFalse();
                call.Arg<JobWorkspaceInput>().JobId.ShouldBe(Lease.JobId);
                call.Arg<JobWorkspaceInput>().FileName.ShouldBe("reviewed-catalog.dat");
                call.Arg<Stream>().ShouldBeSameAs(Document.Content);
                Phase("copy");
                _copied = true;
                return PreparedWorkspace;
            });
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _copied.ShouldBeTrue();
                LeaseReleased.ShouldBeFalse();
                Phase("begin");
                _inside = true;
                return Transaction;
            });
            Acceptance.AddStagedAsync(Arg.Any<ReplaceDatJob>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                _inside.ShouldBeTrue();
                Job = call.Arg<ReplaceDatJob>();
                Job.Id.ShouldBe(Lease.JobId);
                Phase("stage");
                return Task.CompletedTask;
            });
            Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                LeaseReleased.ShouldBeFalse();
                Phase("commit");
                return Task.CompletedTask;
            });
            Transaction.DisposeAsync().Returns(_ => ReleaseTransactionAsync());
            Lease.DisposeAsync().Returns(_ =>
            {
                _inside.ShouldBeFalse();
                LeaseReleased = true;
                Release("lease");
                return ValueTask.CompletedTask;
            });
            Handler = new(Review, Exclusion, Workspace, Acceptance, Work, new Clock(this),
                NullLogger<ApplyReviewedDatReplacementCommandHandler>.Instance);
        }

        private async ValueTask ReleaseTransactionAsync()
        {
            if (TransactionCleanupGate is { } gate)
                await gate;
            LeaseReleased.ShouldBeFalse();
            TransactionReleased = true;
            _inside = false;
            Release("transaction");
        }

        public void Phase(string name)
        {
            Events.Add(name);
            AtPhase?.Invoke(name);
            if (FaultAt == name)
                throw Fault;
        }

        public void Release(string name)
        {
            Events.Add(name + "-dispose");
            if (CleanupFaults || CleanupFaultAt is "all" || CleanupFaultAt == name)
                throw new IOException("cleanup fault " + name);
        }

        public void AssertAllReleased()
        {
            TransactionReleased.ShouldBeTrue();
            LeaseReleased.ShouldBeTrue();
            Document.DisposeCount.ShouldBe(1);
            Document.Content.DisposeCount.ShouldBe(1);
        }

        public sealed class TrackedDocument(Fixture fixture) : IPreparedDatReplacement
        {
            public DatReplacementFacts Facts { get; } = new(7, 8, 9, 42, "Original catalog", "active", "candidate");
            public TrackedStream Content { get; } = new(fixture);
            public int DisposeCount { get; private set; }
            public Stream OpenRead() { fixture.Phase("open"); return Content; }
            public void Dispose() { DisposeCount++; fixture.Release("receipt"); }
        }

        public sealed class TrackedStream(Fixture fixture) : MemoryStream([1, 2, 3], writable: false)
        {
            public int DisposeCount { get; private set; }
            public override ValueTask DisposeAsync()
            {
                DisposeCount++;
                fixture.Release("stream");
                return base.DisposeAsync();
            }
        }

        private sealed class Clock(Fixture fixture) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() { fixture.Phase("clock"); return Now; }
        }
    }
}
