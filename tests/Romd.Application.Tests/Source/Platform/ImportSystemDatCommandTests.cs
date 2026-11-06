using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.ImportSystemDat;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Platform;

public sealed class ImportSystemDatCommandTests
{
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 15, 4, 3, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_InspectionErrorOrCaseSensitiveStale_ReturnsBeforeTransactionAndAcceptance(bool invalid)
    {
        var fixture = new Fixture();
        var rejected = Error.Validation("DatReview.InvalidDocument", "Invalid document.");
        if (invalid)
            fixture.Inspection = rejected;
        using var input = new MemoryStream([1, 2, 3]);

        var result = await fixture.Handler.HandleAsync(new(7, input, Hash.ToUpperInvariant()));

        result.FirstError.Code.ShouldBe(invalid ? rejected.Code : "SystemSetup.Stale");
        fixture.Events.ShouldBe(["inspect"]);
        fixture.Job.ShouldBeNull();
        input.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_MissingOrGloballyInstalled_ReturnsBeforeRewindActorClockAndWorkspace(bool installed)
    {
        var fixture = new Fixture { Exists = installed, Installed = installed, CleanupFaults = true };
        using var input = new MemoryStream([1, 2, 3]);

        var result = await fixture.Handler.HandleAsync(new(7, input, Hash));

        result.FirstError.Code.ShouldBe(installed ? "SystemSetup.Unchanged" : "SystemSetup.NotFound");
        fixture.Events.ShouldBe(installed
            ? ["inspect", "begin", "enable", "installed", "transaction-dispose"]
            : ["inspect", "begin", "enable", "transaction-dispose"]);
        input.Position.ShouldBe(input.Length);
        fixture.TransactionDisposed.ShouldBeTrue();
        fixture.Prepared.ShouldBeNull();
        fixture.CommitAttempted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_NewDocument_PreservesAcceptanceFactsAndCopyInsideTransaction(bool anonymous)
    {
        var fixture = new Fixture { Actor = anonymous ? null : Guid.NewGuid() };
        using var input = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Handler.HandleAsync(new(7, input, Hash), cancellation.Token);

        var job = fixture.Job.ShouldNotBeNull();
        result.Value.ShouldBe(job.Id);
        job.Id.ShouldNotBe(Guid.Empty);
        job.CorrelationId.ShouldNotBe(Guid.Empty);
        job.SourceFilename.ShouldBe("reviewed-catalog.dat");
        job.CreatedAt.ShouldBe(Now);
        job.CreatedByUserId.ShouldBe(fixture.Actor);
        job.PlatformId.ShouldBe(7);
        job.PhaseEnum.ShouldBe(UploadPhase.Pending);
        job.MaxParallelRoms.ShouldBe(4);
        job.AllowUnidentified.ShouldBeFalse();
        job.ArchiveOnly.ShouldBeFalse();
        job.TrackedOnly.ShouldBeFalse();
        job.ImportSourcePath.ShouldBeNull();
        job.ImportMove.ShouldBeFalse();
        fixture.Events.ShouldBe(["inspect", "begin", "enable", "installed", "actor", "clock", "lookup", "lease", "lookup", "copy", "stage", "flush", "commit", "transaction-dispose", "lease-dispose"]);
        fixture.Tokens.ShouldAllBe(token => token == cancellation.Token);
        fixture.Prepared.ShouldNotBeNull().Discarded.ShouldBeFalse();
        input.CanRead.ShouldBeTrue();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("enable")]
    [InlineData("installed")]
    [InlineData("actor")]
    [InlineData("clock")]
    [InlineData("lookup")]
    [InlineData("lease")]
    [InlineData("copy")]
    [InlineData("stage")]
    [InlineData("flush")]
    [InlineData("commit")]
    public async Task Handle_FaultWithCleanupFaults_PreservesExactPrimaryAndRetainedInput(string phase)
    {
        var fixture = new Fixture { FaultAt = phase, CleanupFaults = true };
        using var input = new MemoryStream([1, 2, 3]);

        var error = await Should.ThrowAsync<IOException>(() => fixture.Handler.HandleAsync(new(7, input, Hash)));

        error.ShouldBeSameAs(fixture.Fault);
        fixture.Prepared?.Discarded.ShouldBeFalse();
        fixture.CommitAttempted.ShouldBe(phase == "commit");
        input.CanRead.ShouldBeTrue();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("stage")]
    [InlineData("flush")]
    [InlineData("commit")]
    public async Task Handle_CancellationWithCleanupFaults_PreservesExceptionAndOriginalToken(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException("Cancelled import.", cancellation.Token);
        var fixture = new Fixture { FaultAt = phase, Fault = expected, CleanupFaults = true };
        using var input = new MemoryStream([1, 2, 3]);

        OperationCanceledException? error = null;
        try
        {
            await fixture.Handler.HandleAsync(new(7, input, Hash), cancellation.Token);
        }
        catch (OperationCanceledException caught)
        {
            error = caught;
        }

        error.ShouldBeSameAs(expected);
        error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        fixture.Prepared?.Discarded.ShouldBeFalse();
        fixture.CommitAttempted.ShouldBe(phase == "commit");
        fixture.AssertReleased();
    }

    [Fact]
    public async Task Handle_PreparationRejected_PreservesErrorWithoutStageCommitOrDiscard()
    {
        var rejected = Error.Validation("Upload.Empty", "No upload bytes.");
        var fixture = new Fixture { PreparationError = rejected, CleanupFaults = true };
        using var input = new MemoryStream([1, 2, 3]);

        var result = await fixture.Handler.HandleAsync(new(7, input, Hash));

        result.FirstError.ShouldBe(rejected);
        fixture.Events.ShouldNotContain("stage");
        fixture.Events.ShouldNotContain("flush");
        fixture.CommitAttempted.ShouldBeFalse();
        fixture.AssertReleased();
    }

    [Fact]
    public async Task Handle_CancelledAfterFlush_RollsBackWithoutAttemptingCommitAndKeepsPreparedInput()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture { AfterFlush = cancellation.Cancel, CleanupFaults = true };
        using var input = new MemoryStream([1, 2, 3]);

        var error = await Should.ThrowAsync<OperationCanceledException>(() => fixture.Handler.HandleAsync(new(7, input, Hash), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        fixture.CommitAttempted.ShouldBeFalse();
        fixture.Prepared.ShouldNotBeNull().Discarded.ShouldBeFalse();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AuthoritativeLookupFindsDifferentJob_RejectsWithoutOverwrite(bool underLease)
    {
        var fixture = new Fixture();
        fixture.Lookup = (id, count) => underLease && count == 1 ? null
            : UploadJob.Create("other.dat", Now, requestId: id);
        using var input = new MemoryStream([1, 2, 3]);

        var result = await fixture.Handler.HandleAsync(new(7, input, Hash));

        result.FirstError.Code.ShouldBe("Upload.RequestIdReused");
        fixture.Events.Count(item => item == "lookup").ShouldBe(underLease ? 2 : 1);
        fixture.Events.Contains("lease").ShouldBe(underLease);
        fixture.Events.ShouldNotContain("copy");
        fixture.Job.ShouldBeNull();
        fixture.CommitAttempted.ShouldBeFalse();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CommitOrTransactionDisposalPending_KeepsLeaseUntilSettlement(bool disposal)
    {
        var fixture = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposal)
            fixture.DisposalGate = gate.Task;
        else
            fixture.CommitGate = gate.Task;
        using var input = new MemoryStream([1, 2, 3]);
        var operation = fixture.Handler.HandleAsync(new(7, input, Hash));
        try
        {
            operation.IsCompleted.ShouldBeFalse();
            fixture.LeaseReleased.ShouldBeFalse();
            fixture.TransactionDisposed.ShouldBeFalse();
            fixture.Prepared.ShouldNotBeNull().Discarded.ShouldBeFalse();
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
    public async Task Handle_AcknowledgedCommitAndBothCleanupFaults_ReturnsDurableAcceptance()
    {
        var fixture = new Fixture { CleanupFaults = true };
        using var input = new MemoryStream([1, 2, 3]);

        var result = await fixture.Handler.HandleAsync(new(7, input, Hash));

        result.Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.Prepared.ShouldNotBeNull().Discarded.ShouldBeFalse();
        fixture.AssertReleased();
    }

    private sealed class Fixture : ISystemDatImportStore, IJobWorkspaceExclusion, IUploadWorkspacePreparation, IUploadJobAcceptance, IUnitOfWork
    {
        private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
        private readonly IDatReplacementReview _review = Substitute.For<IDatReplacementReview>();
        private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
        private readonly Transaction _transaction;
        private int _lookups;
        public Fixture()
        {
            _transaction = new(this);
            _review.InspectAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("inspect", call.Arg<CancellationToken>());
                var stream = call.Arg<Stream>();
                stream.Position = stream.Length;
                return Task.FromResult(Inspection);
            });
            _jobs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("lookup", call.Arg<CancellationToken>());
                return Task.FromResult(Lookup(call.Arg<Guid>(), ++_lookups));
            });
            _user.UserId.Returns(_ => { Phase("actor"); return Actor; });
            Handler = new(_review, this, _jobs, this, this, this, this, _user, new Clock(this), NullLogger<ImportSystemDatCommandHandler>.Instance);
        }

        public ImportSystemDatCommandHandler Handler { get; }
        public ErrorOr<DatDocumentInspection> Inspection { get; set; } = new DatDocumentInspection("Catalog", 3,
            new("", Hash, null, null, false, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, [], false));
        public List<string> Events { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<Guid, int, Job?> Lookup { get; set; } = (_, _) => null;
        public bool Exists { get; init; } = true;
        public bool Installed { get; init; }
        public Guid? Actor { get; init; }
        public string? FaultAt { get; init; }
        public Exception Fault { get; init; } = new IOException("Injected import failure.");
        public bool CleanupFaults { get; init; }
        public Error? PreparationError { get; init; }
        public UploadJob? Job { get; private set; }
        public PreparedWorkspace? Prepared { get; private set; }
        public bool TransactionOpened { get; private set; }
        public bool TransactionDisposed { get; private set; }
        public bool LeaseAcquired { get; private set; }
        public bool LeaseReleased { get; private set; }
        public bool CommitAttempted { get; private set; }
        public Task CommitGate { get; set; } = Task.CompletedTask;
        public Task DisposalGate { get; set; } = Task.CompletedTask;
        public Action? AfterFlush { get; init; }

        private void Phase(string name, CancellationToken? token = null)
        {
            Events.Add(name);
            if (token is { } value)
                Tokens.Add(value);
            if (FaultAt == name)
                throw Fault;
        }

        public Task<bool> EnableStagedAsync(int platformId, CancellationToken ct = default)
        {
            TransactionOpened.ShouldBeTrue();
            platformId.ShouldBe(7);
            Phase("enable", ct);
            return Task.FromResult(Exists);
        }

        public Task<bool> IsDocumentInstalledAsync(Sha256 hash, CancellationToken ct = default)
        {
            hash.ShouldBe(Sha256.Parse(Hash));
            Phase("installed", ct);
            return Task.FromResult(Installed);
        }

        public Task<IJobWorkspaceLease> AcquireAsync(Guid jobId, CancellationToken ct = default)
        {
            Phase("lease", ct);
            LeaseAcquired = true;
            return Task.FromResult<IJobWorkspaceLease>(new Lease(this, jobId));
        }

        public Task<ErrorOr<IPreparedJobWorkspace>> PrepareAsync(JobWorkspaceInput input, Stream source, IJobWorkspaceLease lease, CancellationToken ct = default)
        {
            TransactionOpened.ShouldBeTrue();
            TransactionDisposed.ShouldBeFalse();
            LeaseReleased.ShouldBeFalse();
            input.JobId.ShouldBe(lease.JobId);
            input.FileName.ShouldBe("reviewed-catalog.dat");
            source.Position.ShouldBe(0);
            Phase("copy", ct);
            if (PreparationError is { } error)
                return Task.FromResult<ErrorOr<IPreparedJobWorkspace>>(error);
            Prepared = new(input.JobId);
            return Task.FromResult<ErrorOr<IPreparedJobWorkspace>>(Prepared);
        }

        public Task AddStagedAsync(UploadJob job, CancellationToken ct = default)
        {
            LeaseReleased.ShouldBeFalse();
            Prepared.ShouldNotBeNull().JobId.ShouldBe(job.Id);
            Job = job;
            Phase("stage", ct);
            return Task.CompletedTask;
        }

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            Phase("begin", cancellationToken);
            TransactionOpened = true;
            return Task.FromResult<ITransaction>(_transaction);
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            Phase("flush", cancellationToken);
            AfterFlush?.Invoke();
            return Task.CompletedTask;
        }

        public TransactionConflict ConflictOf(Exception error) => throw new NotSupportedException();

        public void AssertReleased()
        {
            TransactionDisposed.ShouldBe(TransactionOpened);
            LeaseReleased.ShouldBe(LeaseAcquired);
        }

        public sealed class PreparedWorkspace(Guid id) : IPreparedJobWorkspace
        {
            public Guid JobId => id;
            public bool Discarded { get; private set; }
            public Task DiscardAsync(CancellationToken ct = default) { Discarded = true; return Task.CompletedTask; }
        }

        private sealed class Transaction(Fixture fixture) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                fixture.LeaseReleased.ShouldBeFalse();
                fixture.CommitAttempted = true;
                fixture.Phase("commit", cancellationToken);
                await fixture.CommitGate;
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public async ValueTask DisposeAsync()
            {
                fixture.LeaseReleased.ShouldBeFalse();
                fixture.Phase("transaction-dispose");
                await fixture.DisposalGate;
                fixture.TransactionDisposed = true;
                if (fixture.CleanupFaults)
                    throw new IOException("Transaction cleanup failed.");
            }
        }

        private sealed class Lease(Fixture fixture, Guid id) : IJobWorkspaceLease
        {
            public Guid JobId => id;
            public ValueTask DisposeAsync()
            {
                fixture.TransactionDisposed.ShouldBeTrue();
                fixture.Phase("lease-dispose");
                fixture.LeaseReleased = true;
                if (fixture.CleanupFaults)
                    throw new IOException("Lease cleanup failed.");
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Clock(Fixture fixture) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() { fixture.Phase("clock"); return Now; }
        }
    }
}
