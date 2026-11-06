using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSubscription;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class ApplyDatSubscriptionCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 11, 10, TimeSpan.FromHours(-3));

    [Fact]
    public async Task Handle_MissingIdentity_BeginsThenReturnsNotFoundWithoutLockingOrReadingEnabledState()
    {
        var fixture = new Fixture { Row = null, Enabled = false, CleanupFaults = true };

        var result = await fixture.Handler.HandleAsync(Command);

        result.FirstError.Code.ShouldBe("DatEnrollment.NotFound");
        fixture.Events.ShouldBe(["begin", "row-read", "transaction-dispose"]);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_ContendedCatalogOrSourceWhileDisabled_ReturnsBusyBeforePinsReplayOrEnabledCheck(bool source)
    {
        var fixture = new Fixture(replacement: source) { Enabled = false, CatalogAvailable = source, SourceAvailable = false };

        var result = await fixture.Handler.HandleAsync(Command with { ActiveSha256 = "wrong" });

        result.FirstError.Code.ShouldBe("DatEnrollment.Busy");
        fixture.Events.ShouldNotContain("enabled");
        fixture.Events.ShouldNotContain("preview");
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AcceptedWhileDisabled_ReturnsReplayOnlyAfterExactPinCheckAndSurvivesDisposal(bool stale)
    {
        var fixture = new Fixture { Enabled = false, CleanupFaults = true };
        var accepted = Guid.NewGuid();
        fixture.Row = fixture.Row! with { Values = fixture.Row!.Values with { JobId = accepted, State = "Applying" } };

        var result = await fixture.Handler.HandleAsync(stale ? Command with { CandidateSha256 = "other" } : Command);

        if (stale)
            result.FirstError.Code.ShouldBe("DatEnrollment.Stale");
        else
            result.Value.ShouldBe(accepted);
        fixture.Events.ShouldNotContain("enabled");
        fixture.Events.ShouldNotContain("clock");
        fixture.CommitAttempted.ShouldBeFalse();
        fixture.AssertReleased();
    }

    [Fact]
    public async Task Handle_DisabledWithoutAcceptedJob_ReturnsUnavailableAfterCatalogLockAndPins()
    {
        var fixture = new Fixture { Enabled = false };

        var result = await fixture.Handler.HandleAsync(Command);

        result.FirstError.Code.ShouldBe("DatEnrollment.Unavailable");
        fixture.Events.ShouldBe(["begin", "row-read", "catalog-lock", "row-read", "enabled", "transaction-dispose"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_InitialOrReplacement_PreservesDuplicateReviewOrderJobFactsAndLeaseThroughDisposal(bool replacement)
    {
        var fixture = new Fixture(replacement);
        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Handler.HandleAsync(Command, cancellation.Token);

        var job = fixture.Job.ShouldNotBeNull();
        result.Value.ShouldBe(job.Id);
        job.Id.ShouldNotBe(Guid.Empty);
        job.CorrelationId.ShouldNotBe(Guid.Empty);
        job.CreatedAt.ShouldBe(Now);
        job.SourceFilename.ShouldBe(replacement ? "reviewed-catalog.dat" : "subscribed-catalog.dat");
        job.Phase.ShouldBe("Pending");
        if (replacement)
        {
            var replace = job.ShouldBeOfType<ReplaceDatJob>();
            replace.ExistingDatId.ShouldBe(9);
            replace.PlatformId.ShouldBeNull();
            replace.CreatedByUserId.ShouldBeNull();
            fixture.Events.ShouldBe(["begin", "row-read", "catalog-lock", "row-read", "source-lock", "row-read", "enabled", "active", "source-lock", "row-read", "active", "platform", "source-enabled", "preview-open", "preview", "preview-dispose", "candidate-open", "review-prepare", "private-open", "clock", "lease", "copy", "stage", "row-stage", "commit", "transaction-dispose", "private-dispose", "receipt-dispose", "candidate-dispose", "lease-dispose"]);
        }
        else
        {
            var upload = job.ShouldBeOfType<UploadJob>();
            upload.PlatformId.ShouldBe(42);
            upload.CreatedByUserId.ShouldBe(fixture.Actor);
            upload.MaxParallelRoms.ShouldBe(4);
            upload.AllowUnidentified.ShouldBeFalse();
            upload.ArchiveOnly.ShouldBeFalse();
            upload.TrackedOnly.ShouldBeFalse();
            fixture.Events.ShouldBe(["begin", "row-read", "catalog-lock", "row-read", "enabled", "platform", "name-match", "preview-open", "preview", "preview-dispose", "candidate-open", "actor", "clock", "lookup", "lease", "lookup", "copy", "stage", "row-stage", "commit", "transaction-dispose", "candidate-dispose", "lease-dispose"]);
        }
        fixture.AcceptedSource.ShouldBe(replacement ? 7 : null);
        fixture.Tokens.ShouldAllBe(token => token == cancellation.Token);
        fixture.PreparedWorkspace.ReceivedCalls().ShouldBeEmpty();
        await fixture.Work.DidNotReceiveWithAnyArgs().FlushAsync(default);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_SecondSourceReloadChangesPinsOrFindsAcceptedJob_RevalidatesBeforeReview(bool replay)
    {
        var fixture = new Fixture(replacement: true) { CleanupFaults = true };
        var accepted = Guid.NewGuid();
        fixture.ReadAt = count => count < 4 ? fixture.Row : fixture.Row! with
        { Values = replay ? fixture.Row!.Values with { JobId = accepted } : fixture.Row!.Values with { CandidateSha256 = "new" } };

        var result = await fixture.Handler.HandleAsync(Command);

        if (replay)
            result.Value.ShouldBe(accepted);
        else
            result.FirstError.Code.ShouldBe("DatEnrollment.Stale");
        fixture.Events.Count(item => item == "source-lock").ShouldBe(2);
        fixture.Events.ShouldNotContain("preview");
        fixture.CommitAttempted.ShouldBeFalse();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unpublished")]
    [InlineData("retired")]
    public async Task Handle_PlatformCannotBeReviewed_ReturnsExistingPlatformErrorBeforeOpeningCandidate(string reason)
    {
        var fixture = new Fixture();
        fixture.Platform = reason == "missing" ? null : new(reason != "unpublished", reason == "retired");

        var result = await fixture.Handler.HandleAsync(Command);

        result.FirstError.Code.ShouldBe("DatEnrollment.PlatformMissing");
        fixture.Events.ShouldNotContain("preview-open");
        fixture.CommitAttempted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("state", "DatEnrollment.Stale")]
    [InlineData("candidate", "DatEnrollment.Stale")]
    [InlineData("bound-missing", "DatEnrollment.Stale")]
    [InlineData("source-disabled", "DatEnrollment.Stale")]
    [InlineData("name-match", "DatEnrollment.Stale")]
    [InlineData("preview-missing", "DatEnrollment.Stale")]
    [InlineData("reopened-missing", "DatEnrollment.Stale")]
    public async Task Handle_ServerHeldReviewPreconditionFails_RejectsBeforeWorkspaceOrJob(string reason, string code)
    {
        var fixture = new Fixture(reason is "bound-missing" or "source-disabled");
        if (reason == "state")
            fixture.Row = fixture.Row! with { Values = fixture.Row!.Values with { State = "CheckFailed" } };
        if (reason == "candidate")
            fixture.Row = fixture.Row! with { Values = fixture.Row!.Values with { CandidateFileId = null } };
        if (reason == "bound-missing")
            fixture.Active = null;
        if (reason == "source-disabled")
            fixture.SourceEnabled = false;
        if (reason == "name-match")
            fixture.NameMatches = [9];
        if (reason == "preview-missing")
            fixture.MissingOpen = 1;
        if (reason == "reopened-missing")
            fixture.MissingOpen = 2;

        var result = await fixture.Handler.HandleAsync(Command);

        result.FirstError.Code.ShouldBe(code);
        fixture.Job.ShouldBeNull();
        fixture.Events.ShouldNotContain("lease");
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("DatReview.InvalidDocument", false)]
    [InlineData("DatReview.Identity", false)]
    [InlineData("Other.Validation", false)]
    [InlineData("DatReview.InvalidDocument", true)]
    [InlineData("DatReview.Identity", true)]
    [InlineData("Other.Validation", true)]
    public async Task Handle_ServerHeldValidation_ConvertsOnlyExistingDocumentAndIdentityErrorsToConflict(string code, bool replacementPreparation)
    {
        var fixture = new Fixture(replacementPreparation) { CleanupFaults = true };
        var error = Error.Validation(code, "review rejection");
        if (replacementPreparation)
            fixture.ReplacementError = error;
        else
            fixture.PreviewError = error;

        var result = await fixture.Handler.HandleAsync(Command);

        result.FirstError.Code.ShouldBe(code);
        result.FirstError.Description.ShouldBe(error.Description);
        result.FirstError.Type.ShouldBe(code == "Other.Validation" ? ErrorType.Validation : ErrorType.Conflict);
        fixture.Events.ShouldNotContain("clock");
        fixture.CommitAttempted.ShouldBeFalse();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_PreviewRecomputedDifferentPins_ReturnsStaleBeforeReopeningOrAcceptance(bool activeChanged)
    {
        var fixture = new Fixture();
        fixture.Preview = fixture.Preview with { ActiveSha256 = activeChanged ? "other" : "active", CandidateSha256 = activeChanged ? "candidate" : "other" };

        var result = await fixture.Handler.HandleAsync(Command);

        result.FirstError.Code.ShouldBe("DatEnrollment.Stale");
        fixture.Events.ShouldNotContain("candidate-open");
        fixture.Events.ShouldNotContain("clock");
    }

    [Theory]
    [InlineData("preview", false)]
    [InlineData("copy", false)]
    [InlineData("stage", false)]
    [InlineData("row-stage", false)]
    [InlineData("commit", false)]
    [InlineData("review-prepare", true)]
    [InlineData("copy", true)]
    [InlineData("stage", true)]
    [InlineData("row-stage", true)]
    [InlineData("commit", true)]
    public async Task Handle_FaultWithCleanupFaults_PreservesExactPrimaryAndNeverDeletesCompletedInputs(string phase, bool replacement)
    {
        var fixture = new Fixture(replacement) { FaultAt = phase, CleanupFaults = true, PreviewCleanupFault = phase == "preview" };

        var error = await Should.ThrowAsync<IOException>(() => fixture.Handler.HandleAsync(Command));

        error.ShouldBeSameAs(fixture.Fault);
        fixture.PreparedWorkspace.ReceivedCalls().ShouldBeEmpty();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CancellationAtCommitWithCleanupFaults_PreservesOriginalExceptionAndToken(bool replacement)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException("subscription cancelled", cancellation.Token);
        var fixture = new Fixture(replacement) { FaultAt = "commit", Fault = expected, CleanupFaults = true };

        OperationCanceledException? error = null;
        try
        {
            await fixture.Handler.HandleAsync(Command, cancellation.Token);
        }
        catch (OperationCanceledException caught)
        {
            error = caught;
        }

        error.ShouldBeSameAs(expected);
        error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        fixture.CommitAttempted.ShouldBeTrue();
        fixture.PreparedWorkspace.ReceivedCalls().ShouldBeEmpty();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_CommitOrDisposalPending_HoldsLeaseAndOwnedInputsUntilSettlement(bool replacement, bool disposal)
    {
        var fixture = new Fixture(replacement);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposal)
            fixture.DisposalGate = gate.Task;
        else
            fixture.CommitGate = gate.Task;
        var operation = fixture.Handler.HandleAsync(Command);
        try
        {
            operation.IsCompleted.ShouldBeFalse();
            fixture.LeaseReleased.ShouldBeFalse();
            fixture.Candidate.DisposeCount.ShouldBe(0);
            fixture.Private.DisposeCount.ShouldBe(0);
            fixture.Receipt.DisposeCount.ShouldBe(0);
        }
        finally
        {
            gate.TrySetResult();
            await operation;
        }
        (await operation).Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AcknowledgedCommitAndOwnedCleanupFaults_ReturnsDurableAcceptance(bool replacement)
    {
        var fixture = new Fixture(replacement) { CleanupFaults = true };

        var result = await fixture.Handler.HandleAsync(Command);

        result.Value.ShouldBe(fixture.Job.ShouldNotBeNull().Id);
        fixture.PreparedWorkspace.ReceivedCalls().ShouldBeEmpty();
        fixture.AssertReleased();
    }

    private static ApplyDatSubscriptionCommand Command => new(1, "active", "candidate");

    private sealed class Fixture
    {
        private readonly IDatSubscriptionCheckReader _reader = Substitute.For<IDatSubscriptionCheckReader>();
        private readonly IDatSubscriptionCheckStore _locks = Substitute.For<IDatSubscriptionCheckStore>();
        private readonly IDatSubscriptionApplyStore _store = Substitute.For<IDatSubscriptionApplyStore>();
        private readonly IDatRepository _dats = Substitute.For<IDatRepository>();
        private readonly ISignedDatCatalogClient _client = Substitute.For<ISignedDatCatalogClient>();
        private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
        private readonly IDatReplacementReview _review = Substitute.For<IDatReplacementReview>();
        private readonly IReviewedDatReplacementPreparation _replacementReview = Substitute.For<IReviewedDatReplacementPreparation>();
        private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
        private readonly IJobWorkspaceExclusion _exclusion = Substitute.For<IJobWorkspaceExclusion>();
        private readonly IUploadWorkspacePreparation _uploads = Substitute.For<IUploadWorkspacePreparation>();
        private readonly IReplaceDatWorkspacePreparation _replacements = Substitute.For<IReplaceDatWorkspacePreparation>();
        private readonly IUploadJobAcceptance _uploadAcceptance = Substitute.For<IUploadJobAcceptance>();
        private readonly IReplaceDatJobAcceptance _replacementAcceptance = Substitute.For<IReplaceDatJobAcceptance>();
        private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
        private readonly ITransaction _transaction = Substitute.For<ITransaction>();
        private readonly IJobWorkspaceLease _lease = Substitute.For<IJobWorkspaceLease>();
        private int _reads;
        private int _opens;
        public Fixture(bool replacement = false)
        {
            Row = new(1, "catalog", new(DatSourceId: replacement ? 7 : null, PlatformId: 42, ExpectedName: "Catalog", State: replacement ? "UpdateAvailable" : "ReadyToImport", CandidateFileId: 11, ActiveSha256: "active", CandidateSha256: "candidate"));
            Active = replacement ? DatFile.Rehydrate(9, "Catalog", "Catalog", null, null, null, DatType.Redump, 42,
                "active.dat", 10, Now, null, 1, 1, 0, 7, DatFileLifecycle.Active, null) : null;
            Preview = new("active", "candidate", null, null, false, 1, 1, 1, 1, 0, 1, 1, 0, 0, 0, 0, [], false);
            PreviewDocument = new(this, "preview", [1, 2, 3]);
            Candidate = new(this, "candidate", [1, 2, 3]);
            Private = new(this, "private", [9, 8, 7]);
            Receipt = new(this);
            _reader.ReadAsync(1, Arg.Any<CancellationToken>()).Returns<DatSubscriptionCheckSnapshot?>(call =>
            { Phase("row-read", call.Arg<CancellationToken>()); return ReadAt?.Invoke(++_reads) ?? Row; });
            _locks.TryLockCatalogStagedAsync("catalog", Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("catalog-lock", call.Arg<CancellationToken>()); return Task.FromResult(CatalogAvailable); });
            _locks.TryLockSourceStagedAsync(7, Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("source-lock", call.Arg<CancellationToken>()); return Task.FromResult(SourceAvailable); });
            _client.Enabled.Returns(_ => { Phase("enabled"); return Enabled; });
            _dats.GetActiveBySourceIdAsync(7, Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("active", call.Arg<CancellationToken>()); return Task.FromResult(Active); });
            _store.ReadPlatformAsync(42, Arg.Any<CancellationToken>()).Returns<DatSubscriptionApplyPlatform?>(call =>
            { Phase("platform", call.Arg<CancellationToken>()); return Platform; });
            _reader.IsSourceEnabledAsync(7, Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("source-enabled", call.Arg<CancellationToken>()); return Task.FromResult(SourceEnabled); });
            _reader.ReadActiveDatIdsForNameAsync(42, "Catalog", Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("name-match", call.Arg<CancellationToken>()); return Task.FromResult(NameMatches); });
            _storage.RetrieveByIdAsync(11, Arg.Any<CancellationToken>()).Returns(call =>
            {
                int opened = ++_opens;
                Phase(opened == 1 ? "preview-open" : "candidate-open", call.Arg<CancellationToken>());
                return Task.FromResult<Stream?>(MissingOpen == opened ? null : opened == 1 ? PreviewDocument : Candidate);
            });
            _review.PreviewInitialAsync("Catalog", Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call => PreviewAsync(call.Arg<Stream>(), call.Arg<CancellationToken>()));
            _review.PreviewAsync(9, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call => PreviewAsync(call.Arg<Stream>(), call.Arg<CancellationToken>()));
            _replacementReview.PrepareAsync(9, Candidate, "active", "candidate", Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("review-prepare", call.Arg<CancellationToken>()); return Task.FromResult<ErrorOr<IPreparedDatReplacement>>(ReplacementError is { } error ? error : Receipt); });
            _user.UserId.Returns(_ => { Phase("actor"); return Actor; });
            _jobs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("lookup", call.Arg<CancellationToken>()); return Task.FromResult<Job?>(null); });
            _exclusion.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("lease", call.Arg<CancellationToken>()); LeaseAcquired = true; return Task.FromResult(_lease); });
            _uploads.PrepareAsync(Arg.Any<JobWorkspaceInput>(), Candidate, _lease, Arg.Any<CancellationToken>()).Returns(call =>
            {
                TransactionOpened.ShouldBeTrue();
                Phase("copy", call.Arg<CancellationToken>());
                call.Arg<JobWorkspaceInput>().FileName.ShouldBe("subscribed-catalog.dat");
                return Task.FromResult(ErrorOrFactory.From(PreparedWorkspace));
            });
            _replacements.PrepareAsync(Arg.Any<JobWorkspaceInput>(), Private, _lease, Arg.Any<CancellationToken>()).Returns(call =>
            {
                TransactionOpened.ShouldBeTrue();
                Phase("copy", call.Arg<CancellationToken>());
                call.Arg<JobWorkspaceInput>().FileName.ShouldBe("reviewed-catalog.dat");
                return Task.FromResult(PreparedWorkspace);
            });
            _uploadAcceptance.AddStagedAsync(Arg.Any<UploadJob>(), Arg.Any<CancellationToken>()).Returns(call =>
            { Job = call.Arg<UploadJob>(); Phase("stage", call.Arg<CancellationToken>()); return Task.CompletedTask; });
            _replacementAcceptance.AddStagedAsync(Arg.Any<ReplaceDatJob>(), Arg.Any<CancellationToken>()).Returns(call =>
            { Job = call.Arg<ReplaceDatJob>(); Phase("stage", call.Arg<CancellationToken>()); return Task.CompletedTask; });
            _store.StageAcceptedAsync(1, Arg.Any<int?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Phase("row-stage", call.Arg<CancellationToken>());
                call.Arg<Guid>().ShouldBe(Job.ShouldNotBeNull().Id);
                AcceptedSource = call.Arg<int?>();
                return Task.CompletedTask;
            });
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(call =>
            { Phase("begin", call.Arg<CancellationToken>()); TransactionOpened = true; return Task.FromResult(_transaction); });
            _transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(call =>
            { CommitAttempted = true; Phase("commit", call.Arg<CancellationToken>()); return CommitGate; });
            _transaction.DisposeAsync().Returns(_ => new ValueTask(DisposeTransactionAsync()));
            _lease.DisposeAsync().Returns(_ =>
            {
                TransactionDisposed.ShouldBeTrue();
                Phase("lease-dispose");
                LeaseReleased = true;
                if (CleanupFaults)
                    throw new IOException("Lease cleanup failed.");
                return ValueTask.CompletedTask;
            });
            Handler = new(_reader, _locks, _store, _dats, _client, _storage, _review, _replacementReview, _jobs,
                _exclusion, _uploads, _replacements, _uploadAcceptance, _replacementAcceptance, Work, _user, new Clock(this), NullLogger<ApplyDatSubscriptionCommandHandler>.Instance);
        }

        public ApplyDatSubscriptionCommandHandler Handler { get; }
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public IPreparedJobWorkspace PreparedWorkspace { get; } = Substitute.For<IPreparedJobWorkspace>();
        public DatSubscriptionCheckSnapshot? Row { get; set; }
        public Func<int, DatSubscriptionCheckSnapshot?>? ReadAt { get; set; }
        public DatFile? Active { get; set; }
        public DatSubscriptionApplyPlatform? Platform { get; set; } = new(true, false);
        public DatReplacementPreview Preview { get; set; }
        public Error? PreviewError { get; set; }
        public Error? ReplacementError { get; set; }
        public bool Enabled { get; init; } = true;
        public bool CatalogAvailable { get; init; } = true;
        public bool SourceAvailable { get; init; } = true;
        public bool SourceEnabled { get; set; } = true;
        public IReadOnlyList<int> NameMatches { get; set; } = [];
        public int MissingOpen { get; set; }
        public Guid Actor { get; } = Guid.NewGuid();
        public List<string> Events { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public string? FaultAt { get; init; }
        public Exception Fault { get; init; } = new IOException("Injected Apply fault.");
        public bool CleanupFaults { get; init; }
        public bool PreviewCleanupFault { get; init; }
        public Job? Job { get; private set; }
        public int? AcceptedSource { get; private set; }
        public bool TransactionOpened { get; private set; }
        public bool TransactionDisposed { get; private set; }
        public bool LeaseAcquired { get; private set; }
        public bool LeaseReleased { get; private set; }
        public bool CommitAttempted { get; private set; }
        public Task CommitGate { get; set; } = Task.CompletedTask;
        public Task DisposalGate { get; set; } = Task.CompletedTask;
        public DocumentStream PreviewDocument { get; }
        public DocumentStream Candidate { get; }
        public DocumentStream Private { get; }
        public ReviewReceipt Receipt { get; }

        private Task<ErrorOr<DatReplacementPreview>> PreviewAsync(Stream stream, CancellationToken ct)
        {
            stream.ShouldBeSameAs(PreviewDocument);
            Phase("preview", ct);
            return Task.FromResult<ErrorOr<DatReplacementPreview>>(PreviewError is { } error ? error : Preview);
        }
        private void Phase(string name, CancellationToken? token = null)
        {
            Events.Add(name);
            if (token is { } value)
                Tokens.Add(value);
            if (name == FaultAt)
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
            if (Events.Contains("candidate-open") && MissingOpen != 2)
                Candidate.DisposeCount.ShouldBe(1);
            if (Events.Contains("private-open"))
            { Private.DisposeCount.ShouldBe(1); Receipt.DisposeCount.ShouldBe(1); }
        }
        public sealed class DocumentStream(Fixture fixture, string name, byte[] bytes) : MemoryStream(bytes, writable: false)
        {
            public int DisposeCount { get; private set; }
            public override async ValueTask DisposeAsync()
            {
                DisposeCount++;
                fixture.Phase(name + "-dispose");
                await base.DisposeAsync();
                if (name == "preview" ? fixture.PreviewCleanupFault : fixture.CleanupFaults)
                    throw new IOException(name + " cleanup failed.");
            }
        }
        public sealed class ReviewReceipt(Fixture fixture) : IPreparedDatReplacement
        {
            public DatReplacementFacts Facts { get; } = new(9, 7, 10, 42, "Catalog", "active", "candidate");
            public int DisposeCount { get; private set; }
            public Stream OpenRead() { fixture.Phase("private-open"); return fixture.Private; }
            public void Dispose()
            {
                DisposeCount++;
                fixture.Phase("receipt-dispose");
                if (fixture.CleanupFaults)
                    throw new IOException("Receipt cleanup failed.");
            }
        }
        private sealed class Clock(Fixture fixture) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() { fixture.Phase("clock"); return Now; }
        }
    }
}
