using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Common.Validation;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Ingestion;

public sealed class ImportFromPathCommandTests
{
    private const string Source = "/allowed/roms";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 9, 8, TimeSpan.FromHours(2));

    [Fact]
    public async Task Handle_NoTrackedTitles_RejectsBeforeOptionalSystemKeyOrPathInspection()
    {
        var fixture = new Fixture { HasTracked = false };

        var result = await fixture.Handler.HandleAsync(new(Source, "unknown", TrackedOnly: true));

        result.FirstError.Code.ShouldBe("Upload.NoTrackedTitles");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("trackedOnly");
        result.FirstError.Description.ShouldBe("Track at least one title before importing tracked-title ROMs.");
        fixture.Events.ShouldBe(["tracked"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    public async Task Handle_UnknownOptionalSystem_RejectsBeforeActorAndPathWithoutEchoingKey(string key)
    {
        var fixture = new Fixture { ResolvedSystem = null };

        var result = await fixture.Handler.HandleAsync(new(Source, key, TrackedOnly: true));

        result.FirstError.Code.ShouldBe("Systems.UnknownKey");
        result.FirstError.Description.ShouldBe("Not a known system key.");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("systemKey");
        fixture.Events.ShouldBe(string.IsNullOrWhiteSpace(key) ? ["tracked"] : ["tracked", "system"]);
    }

    [Fact]
    public async Task Handle_InspectionRejected_PreservesErrorBeforeClockLeaseOrTransaction()
    {
        var error = ImportErrors.PathNotAllowed(Source);
        var fixture = new Fixture { InspectionError = error };

        var result = await fixture.Handler.HandleAsync(new(Source));

        result.FirstError.ShouldBe(error);
        fixture.Events.ShouldBe(["actor", "inspect"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_PreparedInput_PreservesFactsFlagsClockSamplesAndResourceOrder(bool move)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Handler.HandleAsync(new(Source, "psx", 17, true, true, true, move), cancellation.Token);

        var job = fixture.Job.ShouldNotBeNull();
        result.Value.ShouldBe(job.Id);
        job.Id.ShouldNotBe(Guid.Empty);
        job.CorrelationId.ShouldNotBe(Guid.Empty);
        job.SourceFilename.ShouldBe("roms/");
        job.ImportSourcePath.ShouldBe(Source);
        job.ImportMove.ShouldBe(move);
        job.PlatformId.ShouldBe(7);
        job.CreatedByUserId.ShouldBe(fixture.Actor);
        job.CreatedAt.ShouldBe(Now);
        job.MaxParallelRoms.ShouldBe(17);
        job.AllowUnidentified.ShouldBeTrue();
        job.ArchiveOnly.ShouldBeTrue();
        job.TrackedOnly.ShouldBeTrue();
        job.PhaseEnum.ShouldBe(UploadPhase.Pending);
        job.Errors.Select(error => (error.Item, error.Message, error.OccurredAt)).ShouldBe(new[]
        {
            ("skipped-a", "unsafe a", Now.AddSeconds(1)),
            ("skipped-b", "unsafe b", Now.AddSeconds(2)),
            ("copy-issue", "read failed", Now.AddMinutes(1))
        });
        fixture.Events.ShouldBe(["tracked", "system", "actor", "inspect", "clock", "clock", "clock", "lease", "copy", "begin", "stage", "commit", "transaction-dispose", "lease-dispose"]);
        fixture.Tokens.ShouldAllBe(token => token == cancellation.Token);
        fixture.Prepared.DiscardCount.ShouldBe(0);
        await fixture.Work.DidNotReceiveWithAnyArgs().FlushAsync(default);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(33)]
    public async Task Handle_ExistingPathConcurrencySemantics_AcceptsValuesOutsideMultipartRange(int concurrency)
    {
        var fixture = new Fixture { Actor = null };

        var result = await fixture.Handler.HandleAsync(new(Source, MaxParallelRoms: concurrency));

        result.IsError.ShouldBeFalse();
        var job = fixture.Job.ShouldNotBeNull();
        job.MaxParallelRoms.ShouldBe(concurrency);
        job.PlatformId.ShouldBeNull();
        job.CreatedByUserId.ShouldBeNull();
        fixture.Events.ShouldNotContain("tracked");
        fixture.Events.ShouldNotContain("system");
    }

    [Fact]
    public async Task Handle_PreparationRejected_PreservesErrorAndReleasesLeaseWithoutTransaction()
    {
        var error = ImportErrors.NoFilesFound(Source);
        var fixture = new Fixture { PreparationError = error, CleanupFaults = true };

        var result = await fixture.Handler.HandleAsync(new(Source));

        result.FirstError.ShouldBe(error);
        fixture.TransactionOpened.ShouldBeFalse();
        fixture.Prepared.DiscardCount.ShouldBe(0);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("copy")]
    [InlineData("begin")]
    [InlineData("stage")]
    [InlineData("commit")]
    public async Task Handle_FaultAndCleanupFaults_PreservesExactCauseAndDiscardsOnlyBeforeCommitAttempt(string phase)
    {
        var fixture = new Fixture { FaultAt = phase, CleanupFaults = true };

        var error = await Should.ThrowAsync<IOException>(() => fixture.Handler.HandleAsync(new(Source, Move: true)));

        error.ShouldBeSameAs(fixture.Fault);
        fixture.Prepared.DiscardCount.ShouldBe(phase is "begin" or "stage" ? 1 : 0);
        fixture.CommitAttempted.ShouldBe(phase == "commit");
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("begin")]
    [InlineData("stage")]
    [InlineData("commit")]
    public async Task Handle_CancellationAndCleanupFaults_PreservesOriginalExceptionAndToken(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException("Path copy cancelled.", cancellation.Token);
        var fixture = new Fixture { FaultAt = phase, Fault = expected, CleanupFaults = true };

        OperationCanceledException? error = null;
        try
        {
            await fixture.Handler.HandleAsync(new(Source), cancellation.Token);
        }
        catch (OperationCanceledException caught)
        {
            error = caught;
        }

        error.ShouldBeSameAs(expected);
        error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        fixture.Prepared.DiscardCount.ShouldBe(phase is "begin" or "stage" ? 1 : 0);
        fixture.AssertReleased();
    }

    [Fact]
    public async Task Handle_CancelledAfterStaging_DiscardsCompletedInputAfterTransactionDisposalWithoutCommit()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture { AfterStage = cancellation.Cancel, CleanupFaults = true };

        var error = await Should.ThrowAsync<OperationCanceledException>(() => fixture.Handler.HandleAsync(new(Source), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        fixture.CommitAttempted.ShouldBeFalse();
        fixture.Prepared.DiscardCount.ShouldBe(1);
        fixture.Events.TakeLast(3).ShouldBe(["transaction-dispose", "discard", "lease-dispose"]);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CommitOrDisposalPending_KeepsCopiedInputAndManifestUnderLeaseUntilSettled(bool disposal)
    {
        var fixture = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposal)
            fixture.DisposalGate = gate.Task;
        else
            fixture.CommitGate = gate.Task;
        var operation = fixture.Handler.HandleAsync(new(Source, Move: true));
        try
        {
            operation.IsCompleted.ShouldBeFalse();
            fixture.LeaseReleased.ShouldBeFalse();
            fixture.TransactionDisposed.ShouldBeFalse();
            fixture.Prepared.DiscardCount.ShouldBe(0);
        }
        finally
        {
            gate.TrySetResult();
            await operation;
        }
        (await operation).Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.AssertReleased();
    }

    [Fact]
    public async Task Handle_AcknowledgedCommitAndCleanupFaults_ReturnsDurableJobWithoutDiscard()
    {
        var fixture = new Fixture { CleanupFaults = true };

        var result = await fixture.Handler.HandleAsync(new(Source, Move: true));

        result.Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.Prepared.DiscardCount.ShouldBe(0);
        fixture.AssertReleased();
    }

    private sealed class Fixture
    {
        private readonly ITrackedTitleReader _tracked = Substitute.For<ITrackedTitleReader>();
        private readonly IReferenceCatalogService _systems = Substitute.For<IReferenceCatalogService>();
        private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
        private readonly IPathImportPreparation _preparation = Substitute.For<IPathImportPreparation>();
        private readonly IJobWorkspaceExclusion _exclusion = Substitute.For<IJobWorkspaceExclusion>();
        private readonly IUploadJobAcceptance _acceptance = Substitute.For<IUploadJobAcceptance>();
        private readonly ITransaction _transaction = Substitute.For<ITransaction>();
        private readonly IJobWorkspaceLease _lease = Substitute.For<IJobWorkspaceLease>();
        private int _clockCalls;
        public Fixture()
        {
            Prepared = new(this);
            _tracked.HasTrackedAsync(cancellationToken: Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("tracked", call.Arg<CancellationToken>()); return Task.FromResult(HasTracked); });
            _systems.ResolveSystemIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("system", call.Arg<CancellationToken>()); return Task.FromResult(ResolvedSystem); });
            _user.UserId.Returns(_ => { Phase("actor"); return Actor; });
            _preparation.Inspect(Source, Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("inspect", call.Arg<CancellationToken>());
                return InspectionError is { } error ? error : new Plan();
            });
            _exclusion.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("lease", call.Arg<CancellationToken>());
                Prepared.JobId = call.Arg<Guid>();
                LeaseAcquired = true;
                return Task.FromResult(_lease);
            });
            _preparation.PrepareAsync(Arg.Any<IPathImportPlan>(), Arg.Any<Guid>(), Arg.Any<bool>(), _lease, Arg.Any<CancellationToken>()).Returns(call =>
            {
                TransactionOpened.ShouldBeFalse();
                call.Arg<Guid>().ShouldBe(Prepared.JobId);
                Phase("copy", call.Arg<CancellationToken>());
                return Task.FromResult<ErrorOr<PreparedPathImport>>(PreparationError is { } error ? error
                    : new PreparedPathImport(Prepared, [new("copy-issue", "read failed", Now.AddMinutes(1))]));
            });
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("begin", call.Arg<CancellationToken>());
                TransactionOpened = true;
                return Task.FromResult(_transaction);
            });
            _acceptance.AddStagedAsync(Arg.Any<UploadJob>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Job = call.Arg<UploadJob>();
                Job.Id.ShouldBe(Prepared.JobId);
                Phase("stage", call.Arg<CancellationToken>());
                AfterStage?.Invoke();
                return Task.CompletedTask;
            });
            _transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(call =>
            { CommitAttempted = true; Phase("commit", call.Arg<CancellationToken>()); return CommitGate; });
            _transaction.DisposeAsync().Returns(_ => new ValueTask(DisposeTransactionAsync()));
            _lease.DisposeAsync().Returns(_ =>
            {
                TransactionDisposed.ShouldBe(TransactionOpened);
                Phase("lease-dispose");
                LeaseReleased = true;
                if (CleanupFaults)
                    throw new IOException("Lease cleanup failed.");
                return ValueTask.CompletedTask;
            });
            Handler = new(_tracked, _systems, _user, _preparation, _exclusion, _acceptance, Work, new Clock(this), NullLogger<ImportFromPathCommandHandler>.Instance);
        }

        public ImportFromPathCommandHandler Handler { get; }
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public List<string> Events { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public bool HasTracked { get; init; } = true;
        public int? ResolvedSystem { get; init; } = 7;
        public Guid? Actor { get; init; } = Guid.NewGuid();
        public Error? InspectionError { get; init; }
        public Error? PreparationError { get; init; }
        public string? FaultAt { get; init; }
        public Exception Fault { get; init; } = new IOException("Path import failed.");
        public bool CleanupFaults { get; init; }
        public Action? AfterStage { get; init; }
        public UploadJob? Job { get; private set; }
        public PreparedWorkspace Prepared { get; }
        public bool TransactionOpened { get; private set; }
        public bool TransactionDisposed { get; private set; }
        public bool LeaseAcquired { get; private set; }
        public bool LeaseReleased { get; private set; }
        public bool CommitAttempted { get; private set; }
        public Task CommitGate { get; set; } = Task.CompletedTask;
        public Task DisposalGate { get; set; } = Task.CompletedTask;

        private void Phase(string name, CancellationToken? token = null)
        {
            Events.Add(name);
            if (token is { } value)
                Tokens.Add(value);
            if (FaultAt == name)
                throw Fault;
        }

        private async Task DisposeTransactionAsync()
        {
            LeaseReleased.ShouldBeFalse();
            Phase("transaction-dispose");
            await DisposalGate;
            TransactionDisposed = true;
            if (CleanupFaults)
                throw new IOException("Transaction cleanup failed.");
        }

        public void AssertReleased()
        {
            TransactionDisposed.ShouldBe(TransactionOpened);
            LeaseReleased.ShouldBe(LeaseAcquired);
        }

        private sealed class Plan : IPathImportPlan
        {
            public PathImportFacts Facts { get; } = new(Source, "roms/", 1,
                [new("skipped-a", "unsafe a"), new("skipped-b", "unsafe b")]);
        }

        public sealed class PreparedWorkspace(Fixture fixture) : IPreparedJobWorkspace
        {
            public Guid JobId { get; set; }
            public int DiscardCount { get; private set; }
            public Task DiscardAsync(CancellationToken ct = default)
            {
                ct.ShouldBe(CancellationToken.None);
                fixture.TransactionDisposed.ShouldBe(fixture.TransactionOpened);
                fixture.LeaseReleased.ShouldBeFalse();
                fixture.Phase("discard");
                DiscardCount++;
                if (fixture.CleanupFaults)
                    throw new IOException("Workspace cleanup failed.");
                return Task.CompletedTask;
            }
        }

        private sealed class Clock(Fixture fixture) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow()
            { fixture.Phase("clock"); return Now.AddSeconds(fixture._clockCalls++); }
        }
    }
}
