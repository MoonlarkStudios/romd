using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Commands.AcceptUpload;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Application.Common.Validation;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Ingestion;

public sealed class AcceptUploadCommandTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.FromHours(3)).AddTicks(1234567);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("folder/..")]
    [InlineData("folder\\")]
    public async Task Handle_InvalidFileWinsBeforeGenericOptionsTrackingOrOpening(string? name)
    {
        var f = new Fixture();
        var command = new AcceptUploadCommand(UploadKind.Generic,
            name is null ? null : f.Input(name), MaxParallelRoms: 0, TrackedOnly: true, RequestId: Guid.Empty);

        var result = await f.Handler.HandleAsync(command);

        result.FirstError.Code.ShouldBe("Upload.FileRequired");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("file");
        f.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(UploadKind.Generic)]
    [InlineData(UploadKind.Rom)]
    public async Task Handle_SizeWinsBeforeOtherValidationAndReadsNoInput(UploadKind kind)
    {
        var f = new Fixture();
        f.Options.MaxUploadBytes.Returns(2);

        var result = await f.Handler.HandleAsync(new(kind, f.Input(), MaxParallelRoms: 0, TrackedOnly: true));

        result.FirstError.Code.ShouldBe("Upload.FileTooLarge");
        result.FirstError.Description.ShouldBe("File must not exceed 2 bytes.");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("file");
        f.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task Handle_GenericConcurrencyWinsBeforeIdentityAndTracking(int concurrency)
    {
        var f = new Fixture();

        var result = await f.Handler.HandleAsync(new(UploadKind.Generic, f.Input(),
            MaxParallelRoms: concurrency, TrackedOnly: true, RequestId: Guid.Empty));

        result.FirstError.Code.ShouldBe("Upload.InvalidConcurrency");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("maxParallelRoms");
        f.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_GenericInvalidIdentityWinsBeforeTracking(bool empty)
    {
        var f = new Fixture();

        var result = await f.Handler.HandleAsync(new(UploadKind.Generic, f.Input(),
            TrackedOnly: true, RequestId: empty ? Guid.Empty : Guid.NewGuid()));

        result.FirstError.Code.ShouldBe("Upload.InvalidIdentity");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("requestId");
        f.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(UploadKind.Generic)]
    [InlineData(UploadKind.Rom)]
    public async Task Handle_NoTrackedTitlesWinsBeforeClassificationAndOpening(UploadKind kind)
    {
        var f = new Fixture();

        var result = await f.Handler.HandleAsync(new(kind, f.Input(), TrackedOnly: true));

        result.FirstError.Code.ShouldBe("Upload.NoTrackedTitles");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("trackedOnly");
        f.Events.ShouldBe(["tracked"]);
    }

    [Theory]
    [InlineData(UploadKind.Dat, FileType.Unknown, "Upload.InvalidDatFile")]
    [InlineData(UploadKind.Dat, FileType.Rom, "Upload.InvalidDatFile")]
    [InlineData(UploadKind.Dat, FileType.Archive, "Upload.InvalidDatFile")]
    [InlineData(UploadKind.Rom, FileType.Dat, "Upload.InvalidRomFile")]
    public async Task Handle_ClassificationRejectionOpensOnlyItsOwnStream(UploadKind kind, FileType type, string code)
    {
        var f = new Fixture { Type = type };
        var input = f.Input("folder\\original.dat");

        var result = await f.Handler.HandleAsync(new(kind, input));

        result.FirstError.Code.ShouldBe(code);
        FieldErrors.FieldOf(result.FirstError).ShouldBe("file");
        input.Streams.ShouldHaveSingleItem().CanRead.ShouldBeFalse();
        f.ClassifiedFilename.ShouldBe("folder\\original.dat");
        f.Events.ShouldBe(["open1", "classify", "stream1-dispose"]);
    }

    [Theory]
    [InlineData(UploadKind.Dat, FileType.Dat)]
    [InlineData(UploadKind.Rom, FileType.Unknown)]
    [InlineData(UploadKind.Rom, FileType.Rom)]
    [InlineData(UploadKind.Rom, FileType.Archive)]
    public async Task Handle_ValidatedModeUsesDistinctStreamsAndPreservesItsDefaults(UploadKind kind, FileType type)
    {
        var f = new Fixture { Type = type };
        f.Options.MaxUploadBytes.Returns(kind == UploadKind.Dat ? 1 : 10);
        var input = f.Input("folder\\original.dat");

        var result = await f.Handler.HandleAsync(new(kind, input, PlatformId: kind == UploadKind.Dat ? 42 : null));

        var job = f.Job.ShouldNotBeNull();
        result.Value.ShouldBe(job.Id);
        job.SourceFilename.ShouldBe("original.dat");
        job.PlatformId.ShouldBe(kind == UploadKind.Dat ? 42 : null);
        job.CreatedByUserId.ShouldBe(f.Actor);
        job.CreatedAt.ShouldBe(Now);
        job.CreatedAt.Offset.ShouldBe(Now.Offset);
        job.MaxParallelRoms.ShouldBe(4);
        job.AllowUnidentified.ShouldBeFalse();
        job.ArchiveOnly.ShouldBeFalse();
        job.TrackedOnly.ShouldBeFalse();
        input.Streams.Count.ShouldBe(2);
        f.ClassifiedFilename.ShouldBe("folder\\original.dat");
        input.Streams.ShouldAllBe(stream => !stream.CanRead);
        f.Events.ShouldBe(["open1", "classify", "open2", "actor", "clock", "lookup", "lease", "lookup", "copy",
            "begin", "stage", "commit", "transaction-dispose", "stream2-dispose", "stream1-dispose", "lease-dispose"]);
        await f.Work.DidNotReceiveWithAnyArgs().FlushAsync(default);
        await f.Prepared.DidNotReceiveWithAnyArgs().DiscardAsync(default);
    }

    [Fact]
    public async Task Handle_GenericPreservesExactOptionsIdentityAndClockBeforeAnyLookup()
    {
        var f = new Fixture { Tracked = true };
        var id = Guid.NewGuid();
        var batch = Guid.NewGuid();

        var result = await f.Handler.HandleAsync(new(UploadKind.Generic, f.Input("folder/pack.zip"), 42,
            32, true, true, true, id, batch));

        var job = f.Job.ShouldNotBeNull();
        result.Value.ShouldBe(id);
        job.Id.ShouldBe(id);
        job.CorrelationId.ShouldBe(batch);
        job.PlatformId.ShouldBe(42);
        job.CreatedByUserId.ShouldBe(f.Actor);
        job.CreatedAt.ShouldBe(Now);
        job.MaxParallelRoms.ShouldBe(32);
        job.AllowUnidentified.ShouldBeTrue();
        job.ArchiveOnly.ShouldBeTrue();
        job.TrackedOnly.ShouldBeTrue();
        f.Events.ShouldBe(["tracked", "open1", "actor", "clock", "lookup", "lease", "lookup", "copy",
            "begin", "stage", "commit", "transaction-dispose", "stream1-dispose", "lease-dispose"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_MatchingReplayAtEitherLookupDoesNotReadOrOverwriteAcceptedInputs(bool underLease)
    {
        var f = new Fixture();
        var previous = UploadJob.Create("pack.zip", Now, createdByUserId: f.Actor);
        f.Previous = previous;
        f.PreviousAt = underLease ? 2 : 1;

        var result = await f.Handler.HandleAsync(new(UploadKind.Generic, f.Input(), RequestId: previous.Id, BatchId: previous.CorrelationId));

        result.Value.ShouldBe(previous.Id);
        f.Job.ShouldBeNull();
        f.Events.ShouldBe(underLease
            ? ["open1", "actor", "clock", "lookup", "lease", "lookup", "stream1-dispose", "lease-dispose"]
            : ["open1", "actor", "clock", "lookup", "stream1-dispose"]);
        f.Work.ReceivedCalls().ShouldBeEmpty();
        f.Workspace.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("family")]
    [InlineData("actor")]
    [InlineData("filename")]
    [InlineData("batch")]
    [InlineData("platform")]
    [InlineData("parallel")]
    [InlineData("unidentified")]
    [InlineData("archive")]
    [InlineData("tracked")]
    public async Task Handle_ReplayMismatchRejectsEveryExistingIdentityComparison(string changed)
    {
        var f = new Fixture();
        var previous = UploadJob.Create(changed == "filename" ? "different.zip" : "pack.zip", Now,
            changed == "platform" ? 42 : null, changed == "actor" ? Guid.NewGuid() : f.Actor);
        if (changed == "parallel")
            previous.SetMaxParallelRoms(8);
        if (changed == "unidentified")
            previous.SetAllowUnidentified(true);
        if (changed == "archive")
            previous.SetArchiveOnly(true);
        if (changed == "tracked")
            previous.SetTrackedOnly(true);
        f.Previous = changed == "family" ? ReplaceDatJob.Create(7, "pack.zip", Now) : previous;

        var result = await f.Handler.HandleAsync(new(UploadKind.Generic, f.Input(), RequestId: previous.Id,
            BatchId: changed == "batch" ? Guid.NewGuid() : previous.CorrelationId));

        result.FirstError.ShouldBe(UploadErrors.RequestIdReused());
        f.Workspace.ReceivedCalls().ShouldBeEmpty();
        f.Acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_DurableReplayAtEitherLookupSurvivesStreamAndLeaseCleanupFaults(bool underLease)
    {
        var f = new Fixture { CleanupFaults = true };
        var previous = UploadJob.Create("pack.zip", Now, createdByUserId: f.Actor);
        f.Previous = previous;
        f.PreviousAt = underLease ? 2 : 1;

        var result = await f.Handler.HandleAsync(new(UploadKind.Generic, f.Input(), RequestId: previous.Id, BatchId: previous.CorrelationId));

        result.Value.ShouldBe(previous.Id);
        f.Events.ShouldContain("stream1-dispose");
        if (underLease)
            f.Events.ShouldContain("lease-dispose");
        f.Work.ReceivedCalls().ShouldBeEmpty();
        f.Workspace.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("open1")]
    [InlineData("lease")]
    [InlineData("copy")]
    [InlineData("begin")]
    [InlineData("stage")]
    [InlineData("commit")]
    public async Task Handle_FaultAndCleanupFailurePreserveExactPrimaryWithoutDiscard(string phase)
    {
        var f = new Fixture { FaultAt = phase, CleanupFaults = true };

        var error = await Should.ThrowAsync<IOException>(() => f.Handler.HandleAsync(new(UploadKind.Generic, f.Input())));

        error.ShouldBeSameAs(f.Fault);
        await f.Prepared.DidNotReceiveWithAnyArgs().DiscardAsync(default);
        if (phase is "stage" or "commit")
            f.Events.ShouldContain("transaction-dispose");
        if (phase is not "open1" and not "lease")
            f.Events.ShouldContain("lease-dispose");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CancellationDuringCopyOrStagingPreservesTokenAndDoesNotCommit(bool afterStage)
    {
        var f = new Fixture { CleanupFaults = true };
        using var cancellation = new CancellationTokenSource();
        f.AtPhase = phase =>
        {
            if (phase != (afterStage ? "stage" : "copy"))
                return;
            cancellation.Cancel();
            if (!afterStage)
                cancellation.Token.ThrowIfCancellationRequested();
        };

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            f.Handler.HandleAsync(new(UploadKind.Generic, f.Input()), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        f.Events.ShouldNotContain("commit");
        await f.Prepared.DidNotReceiveWithAnyArgs().DiscardAsync(default);
    }

    [Fact]
    public async Task Handle_AcknowledgedCommitSurvivesAllCleanupFailures()
    {
        var f = new Fixture { CleanupFaults = true, Type = FileType.Dat };

        var result = await f.Handler.HandleAsync(new(UploadKind.Dat, f.Input()));

        result.Value.ShouldBe(f.Job.ShouldNotBeNull().Id);
        f.Events.TakeLast(4).ShouldBe(["transaction-dispose", "stream2-dispose", "stream1-dispose", "lease-dispose"]);
        await f.Prepared.DidNotReceiveWithAnyArgs().DiscardAsync(default);
    }

    [Fact]
    public async Task Handle_TransactionDisposalPendingKeepsStreamsAndLeaseUntilSettlement()
    {
        var f = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.CleanupGate = gate.Task;
        var input = f.Input();
        var operation = f.Handler.HandleAsync(new(UploadKind.Generic, input));
        try
        {
            operation.IsCompleted.ShouldBeFalse();
            input.Streams.ShouldHaveSingleItem().CanRead.ShouldBeTrue();
            f.Events.ShouldNotContain("lease-dispose");
        }
        finally
        {
            gate.TrySetResult();
            await operation;
        }
        (await operation).Value.ShouldBe(f.Job.ShouldNotBeNull().Id);
        f.Events.TakeLast(3).ShouldBe(["transaction-dispose", "stream1-dispose", "lease-dispose"]);
    }

    private sealed class Fixture
    {
        public ITrackedTitleReader Titles { get; } = Substitute.For<ITrackedTitleReader>();
        public IFileClassifier Classifier { get; } = Substitute.For<IFileClassifier>();
        public IJobRepository Jobs { get; } = Substitute.For<IJobRepository>();
        public IJobWorkspaceExclusion Exclusion { get; } = Substitute.For<IJobWorkspaceExclusion>();
        public IUploadWorkspacePreparation Workspace { get; } = Substitute.For<IUploadWorkspacePreparation>();
        public IPreparedJobWorkspace Prepared { get; } = Substitute.For<IPreparedJobWorkspace>();
        public IUploadJobAcceptance Acceptance { get; } = Substitute.For<IUploadJobAcceptance>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        private ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        private IJobWorkspaceLease Lease { get; } = Substitute.For<IJobWorkspaceLease>();
        public IRomdOptions Options { get; } = Substitute.For<IRomdOptions>();
        public Guid Actor { get; } = Guid.NewGuid();
        public List<string> Events { get; } = [];
        public IOException Fault { get; } = new("primary upload failure");
        public string? FaultAt { get; init; }
        public bool CleanupFaults { get; init; }
        public FileType Type { get; init; } = FileType.Rom;
        public bool Tracked { get; init; }
        public Job? Previous { get; set; }
        public int PreviousAt { get; set; } = 1;
        public UploadJob? Job { get; private set; }
        public string? ClassifiedFilename { get; private set; }
        public Action<string>? AtPhase { get; set; }
        public Task? CleanupGate { get; set; }
        public AcceptUploadCommandHandler Handler { get; }
        private int _lookups;
        private Guid _leaseId;

        public Fixture()
        {
            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(_ => { Phase("actor"); return Actor; });
            Titles.HasTrackedAsync(null, Arg.Any<CancellationToken>()).Returns(_ => { Phase("tracked"); return Tracked; });
            Classifier.ClassifyAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("classify");
                ClassifiedFilename = call.Arg<string>();
                var stream = call.Arg<Stream>();
                stream.Position = stream.Length;
                return new FileClassification(Type, null, 1);
            });
            Jobs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Phase("lookup");
                return Task.FromResult(++_lookups >= PreviousAt ? Previous : null);
            });
            Lease.JobId.Returns(_ => _leaseId);
            Exclusion.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("lease");
                _leaseId = call.Arg<Guid>();
                return Lease;
            });
            Workspace.PrepareAsync(Arg.Any<JobWorkspaceInput>(), Arg.Any<Stream>(), Lease, Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("copy");
                call.Arg<JobWorkspaceInput>().JobId.ShouldBe(_leaseId);
                call.Arg<Stream>().Position.ShouldBe(0);
                return Task.FromResult(ErrorOrFactory.From(Prepared));
            });
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ => { Phase("begin"); return Transaction; });
            Acceptance.AddStagedAsync(Arg.Any<UploadJob>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Job = call.Arg<UploadJob>();
                Job.Id.ShouldBe(_leaseId);
                Phase("stage");
                return Task.CompletedTask;
            });
            Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ => { Phase("commit"); return Task.CompletedTask; });
            Transaction.DisposeAsync().Returns(_ => ReleaseTransactionAsync());
            Lease.DisposeAsync().Returns(_ => { Release("lease"); return ValueTask.CompletedTask; });
            Handler = new(Titles, Classifier, Jobs, Exclusion, Workspace, Acceptance, Work, user,
                Options, new Clock(this), NullLogger<AcceptUploadCommandHandler>.Instance);
        }

        public Input Input(string name = "pack.zip") => new(this, name);
        private async ValueTask ReleaseTransactionAsync()
        {
            if (CleanupGate is { } gate)
                await gate;
            Release("transaction");
        }
        public void Phase(string name) { Events.Add(name); AtPhase?.Invoke(name); if (FaultAt == name) throw Fault; }
        public void Release(string name) { Events.Add(name + "-dispose"); if (CleanupFaults) throw new IOException("cleanup " + name); }
        private sealed class Clock(Fixture fixture) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() { fixture.Phase("clock"); return Now; }
        }
    }

    private sealed class Input(Fixture fixture, string filename) : IUploadInput
    {
        public string FileName { get; } = filename;
        public long Length => 3;
        public List<Stream> Streams { get; } = [];
        public Stream OpenRead()
        {
            int index = Streams.Count + 1;
            fixture.Phase("open" + index);
            var stream = new OwnedStream(fixture, index);
            Streams.Add(stream);
            return stream;
        }
    }

    private sealed class OwnedStream(Fixture fixture, int index) : MemoryStream([1, 2, 3])
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            fixture.Release("stream" + index);
        }
    }
}
