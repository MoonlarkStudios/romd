using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.CheckDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.CreateDatSubscription;
using Romd.Admin.Application.Source.Dat.Subscriptions;
using Romd.Admin.Application.Storage.Files;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class DatSubscriptionCheckCommandTests
{
    private static readonly DateTimeOffset CheckedAt = DateTimeOffset.Parse("2026-11-04T12:00:00.123456Z");
    private static readonly DateTimeOffset ScheduledAt = CheckedAt.AddHours(2);

    [Theory]
    [InlineData(0, true, 1, 1)]
    [InlineData(1, true, 2, 2)]
    [InlineData(2, true, 3, 4)]
    [InlineData(3, true, 4, 8)]
    [InlineData(4, true, 5, 16)]
    [InlineData(5, true, 6, 24)]
    [InlineData(6, true, 6, 24)]
    [InlineData(6, false, 0, 24)]
    public void Schedule_ExplicitFailureHistoryAndTime_PreservesBackoff(int previous, bool failed, int failures, int hours)
    {
        var result = DatSubscriptionCheckSchedule.Advance(previous, failed, CheckedAt);
        result.ConsecutiveFailures.ShouldBe(failures);
        result.NextCheckAt.ShouldBe(CheckedAt.AddHours(hours));
    }

    [Fact]
    public async Task Create_DisabledReaderWithExistingEnrollment_ReplaysWithoutTransactionOrExternalEffects()
    {
        var context = new Context();
        context.Reader.ReadCatalogAsync("catalog", Arg.Any<CancellationToken>()).Returns(new[] { context.Row });
        context.Client.Enabled.Returns(false);

        var result = await context.Create().HandleAsync(new("catalog"));

        result.IsError.ShouldBeFalse();
        result.Value.Created.ShouldBeFalse();
        result.Value.Subscription.Id.ShouldBe(1);
        await context.Reader.Received(1).ReadReplayStatusAsync(context.Row, Arg.Any<CancellationToken>());
        await context.Reader.DidNotReceive().ReadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        context.Unit.ReceivedCalls().ShouldBeEmpty();
        await context.Client.DidNotReceive().DiscoverAsync(Arg.Any<CancellationToken>());
        context.Storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_MissingRowBeforeDisabledReader_ReturnsNotFoundWithoutTransaction()
    {
        var context = new Context();
        context.Reader.ReadAsync(1, Arg.Any<CancellationToken>()).Returns((DatSubscriptionCheckSnapshot?)null);
        context.Client.Enabled.Returns(false);

        var result = await context.Check().HandleAsync(new(1));

        result.FirstError.Code.ShouldBe("DatEnrollment.NotFound");
        context.Unit.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("source")]
    [InlineData("due")]
    [InlineData("running")]
    public async Task Check_ContendedOrIneligible_ReturnsBusyBeforeDiscoveryOrFetch(string reason)
    {
        var context = new Context();
        if (reason == "catalog")
            context.Store.TryLockCatalogStagedAsync("catalog", Arg.Any<CancellationToken>()).Returns(false);
        if (reason == "source")
        {
            context.Reader.ReadAsync(1, Arg.Any<CancellationToken>()).Returns(context.Row with { Values = context.Row.Values with { DatSourceId = 7 } });
            context.Store.TryLockSourceStagedAsync(7, Arg.Any<CancellationToken>()).Returns(false);
        }
        if (reason == "due")
            context.Reader.IsCheckDueAsync(1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        if (reason == "running")
            context.Reader.HasRunningJobAsync(context.Row.Values.JobId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await context.Check().HandleAsync(new(1, reason == "due" ? DatSubscriptionCheckMode.Scheduled : DatSubscriptionCheckMode.Manual));

        result.FirstError.Code.ShouldBe("DatEnrollment.Busy");
        await context.Client.DidNotReceive().DiscoverAsync(Arg.Any<CancellationToken>());
        await context.Client.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        context.Storage.ReceivedCalls().ShouldBeEmpty();
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("fetch")]
    [InlineData("review")]
    public async Task Check_ExpectedFailure_CommitsBackoffAndRetainsExactPreviousCandidateAndJob(string failure)
    {
        var context = new Context();
        if (failure == "directory")
            context.Client.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(Error.Failure("test", "private"));
        if (failure == "fetch")
            context.Client.FetchAsync("catalog", "psx", "Sony", Arg.Any<CancellationToken>()).Returns(Error.Failure("test", "private"));
        if (failure == "review")
            context.Review.PreviewInitialAsync("Sony", Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(Error.Validation("test", "invalid document"));

        var result = await context.Check().HandleAsync(new(1));

        result.IsError.ShouldBeFalse();
        var values = context.Written.ShouldNotBeNull();
        values.State.ShouldBe("CheckFailed");
        values.CandidateFileId.ShouldBe(context.Row.Values.CandidateFileId);
        values.CandidateSha256.ShouldBe(context.Row.Values.CandidateSha256);
        values.ActiveSha256.ShouldBe(context.Row.Values.ActiveSha256);
        values.JobId.ShouldBe(context.Row.Values.JobId);
        values.LastCheckedAt.ShouldBe(CheckedAt);
        values.NextCheckAt.ShouldBe(ScheduledAt.AddHours(4));
        values.ConsecutiveCheckFailures.ShouldBe(3);
        values.Message.ShouldBe(failure == "review" ? "invalid document" : DatEnrollmentErrors.CheckFailed().Description);
        if (failure == "directory")
            values.ExpectedName.ShouldBe("Original");
        else
            values.ExpectedName.ShouldBe("Sony");
        await context.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await context.Unit.Received(1).FlushAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_MissingDirectory_RejectsWithoutStagingOrCommit()
    {
        var context = new Context();
        context.Client.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<PublishedDatCatalog>());

        var result = await context.Create().HandleAsync(new("catalog"));

        result.FirstError.Code.ShouldBe("DatEnrollment.Unavailable");
        context.Written.ShouldBeNull();
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_ExpectedFetchOrReviewFailure_CreatesFailedEnrollmentWithBackoff(bool reviewFailure)
    {
        var context = new Context();
        if (reviewFailure)
            context.Review.PreviewInitialAsync("Sony", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Error.Validation("test", "invalid document"));
        else
            context.Client.FetchAsync("catalog", "psx", "Sony", Arg.Any<CancellationToken>()).Returns(Error.Failure("test", "private"));

        var result = await context.Create().HandleAsync(new("catalog"));

        result.IsError.ShouldBeFalse();
        result.Value.Created.ShouldBeTrue();
        result.Value.Subscription.State.ShouldBe("CheckFailed");
        context.Written.ShouldNotBeNull().CandidateFileId.ShouldBeNull();
        context.Written.ConsecutiveCheckFailures.ShouldBe(1);
        context.Written.NextCheckAt.ShouldBe(ScheduledAt.AddHours(1));
        await context.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_AnotherEnrollmentWinsBeforeLock_ReplaysWithoutCheckingOrCommit()
    {
        var context = new Context();
        context.Reader.ReadCatalogAsync("catalog", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DatSubscriptionCheckSnapshot>(), new[] { context.Row });

        var result = await context.Create().HandleAsync(new("catalog"));

        result.Value.Created.ShouldBeFalse();
        await context.Unit.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await context.Client.DidNotReceive().DiscoverAsync(Arg.Any<CancellationToken>());
        context.Storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_VerifiedCandidate_OwnerFlushesFileAndSubscriptionBeforeReadingAndCommit(bool create)
    {
        var context = new Context();

        var result = create ? (await context.Create().HandleAsync(new("catalog"))).Value.Subscription
            : (await context.Check().HandleAsync(new(1))).Value;

        result.LastCheckedAt.ShouldBe(CheckedAt);
        result.NextCheckAt.ShouldBe(ScheduledAt.AddDays(1));
        context.Written.ShouldNotBeNull().State.ShouldBe("ReadyToImport");
        context.Written.CandidateFileId.ShouldBe(55);
        context.Written.CandidateSha256.ShouldBe("new-candidate");
        context.Written.ActiveSha256.ShouldBeEmpty();
        context.Written.JobId.ShouldBeNull();
        context.Events.ShouldBe(["publish", "flush", "file-id", "stage", "flush", "subscription-id", "response", "commit"]);
    }

    [Fact]
    public async Task Handle_FetchedBufferMutatesDuringStaging_PreparedStreamKeepsVerifiedBytes()
    {
        var context = new Context();
        byte[] fetched = [1, 2, 3];
        context.Client.FetchAsync("catalog", "psx", "Sony", Arg.Any<CancellationToken>()).Returns(fetched);
        context.Storage.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            fetched[0] = 99;
            using var copy = new MemoryStream();
            await call.Arg<Stream>().CopyToAsync(copy);
            copy.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
            return context.StagedFile;
        });

        (await context.Create().HandleAsync(new("catalog"))).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_ResponseReadFails_PreservesCauseAndDoesNotCommit(bool cancel)
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("response read failed");
        context.Reader.ReadWrittenStatusAsync(context.Identity, Arg.Any<CancellationToken>()).Returns<Task<DatCatalogSubscription>>(_ => throw cause);

        var error = await CaptureAsync(() => context.Create().HandleAsync(new("catalog"), cancellation.Token));

        error.ShouldBeSameAs(cause);
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await context.Transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Handle_CommitIncomplete_DoesNotReportAcceptanceUntilCommitCompletes()
    {
        var context = new Context();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var result = context.Create().HandleAsync(new("catalog"));
        result.IsCompleted.ShouldBeFalse();
        gate.SetResult();
        (await result).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CleanupFailsAfterCommit_ReturnsDurableResponse(bool create)
    {
        var context = new Context();
        context.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("cleanup failed")));

        if (create)
            (await context.Create().HandleAsync(new("catalog"))).IsError.ShouldBeFalse();
        else
            (await context.Check().HandleAsync(new(1))).IsError.ShouldBeFalse();
        await context.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Handle_PrimaryWithOutcomeAndTransactionCleanupFaults_PreservesCauseAndSettlementOrder(bool create, bool atCommit, bool cancel)
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("primary cancellation", cancellation.Token)
            : new IOException("primary check fault");
        if (atCommit)
            context.Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(cause));
        else
            context.Reader.ReadWrittenStatusAsync(context.Identity, Arg.Any<CancellationToken>())
                .Returns<Task<DatCatalogSubscription>>(_ => throw cause);
        context.StagedFile.DisposeAsync().Returns(_ =>
        {
            context.Events.Add("outcome-dispose");
            return ValueTask.FromException(new IOException("secondary staged-file cleanup"));
        });
        context.Transaction.DisposeAsync().Returns(_ =>
        {
            context.Events.Add("transaction-dispose");
            return ValueTask.FromException(new IOException("secondary transaction cleanup"));
        });

        var error = await CaptureAsync(async () =>
        {
            if (create)
                await context.Create().HandleAsync(new("catalog"), cancellation.Token);
            else
                await context.Check().HandleAsync(new(1), cancellation.Token);
        });

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        context.Events.TakeLast(2).ShouldBe(["outcome-dispose", "transaction-dispose"]);
        await context.Unit.Received(1).BeginTransactionAsync(cancellation.Token);
        await context.Transaction.Received(atCommit ? 1 : 0).CommitAsync(cancellation.Token);
        await context.StagedFile.Received(1).DisposeAsync();
        await context.Transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AcknowledgedCommitAndBothCleanupFaults_ReturnsDurableResponse(bool create)
    {
        var context = new Context();
        context.StagedFile.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("staged cleanup")));
        context.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("transaction cleanup")));

        if (create)
            (await context.Create().HandleAsync(new("catalog"))).Value.Subscription.State.ShouldBe("ReadyToImport");
        else
            (await context.Check().HandleAsync(new(1))).Value.State.ShouldBe("ReadyToImport");

        await context.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await context.StagedFile.Received(1).DisposeAsync();
        await context.Transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Create_ReplayUnderLockAndCleanupFault_DoesNotCommitOrHideCleanupFailure()
    {
        var context = new Context();
        int reads = 0;
        context.Reader.ReadCatalogAsync("catalog", Arg.Any<CancellationToken>()).Returns(_ =>
            ++reads == 1 ? Array.Empty<DatSubscriptionCheckSnapshot>() : new[] { context.Row });
        var cleanup = new IOException("uncommitted replay cleanup");
        context.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(cleanup));

        var error = await CaptureAsync(() => context.Create().HandleAsync(new("catalog")));

        error.ShouldBeSameAs(cleanup);
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        context.Client.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == nameof(ISignedDatCatalogClient.FetchAsync));
        await context.Transaction.Received(1).DisposeAsync();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Context
    {
        public IDatSubscriptionCheckReader Reader { get; } = Substitute.For<IDatSubscriptionCheckReader>();
        public IDatSubscriptionCheckStore Store { get; } = Substitute.For<IDatSubscriptionCheckStore>();
        public IDatRepository Dats { get; } = Substitute.For<IDatRepository>();
        public ISignedDatCatalogClient Client { get; } = Substitute.For<ISignedDatCatalogClient>();
        public IDatReplacementReview Review { get; } = Substitute.For<IDatReplacementReview>();
        public IFileStorageService Storage { get; } = Substitute.For<IFileStorageService>();
        public IUnitOfWork Unit { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public IStagedFile StagedFile { get; } = Substitute.For<IStagedFile>();
        public IStagedDatSubscriptionCheck Identity { get; } = Substitute.For<IStagedDatSubscriptionCheck>();
        public DatSubscriptionCheckSnapshot Row { get; } = new(1, "catalog", new(ExpectedName: "Original", CandidateFileId: 90,
            CandidateSha256: "old-candidate", ActiveSha256: "old-active", JobId: Guid.NewGuid(), ConsecutiveCheckFailures: 2));
        public DatSubscriptionCheckValues? Written { get; private set; }
        public List<string> Events { get; } = [];
        private readonly TimeProvider time = new SequenceClock();
        public Context()
        {
            Reader.ReadAsync(1, Arg.Any<CancellationToken>()).Returns(Row);
            Reader.ReadCatalogAsync("catalog", Arg.Any<CancellationToken>()).Returns(Array.Empty<DatSubscriptionCheckSnapshot>());
            Reader.ReadPlatformDefinitionAsync("psx", Arg.Any<CancellationToken>()).Returns(new DatSubscriptionCheckPlatform(7, false));
            Reader.ReadActiveDatIdsForNameAsync(7, "Sony", Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
            Reader.ReadActiveDatIdsForDocumentAsync(90, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
            Reader.IsCheckDueAsync(1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
            Store.TryLockCatalogStagedAsync("catalog", Arg.Any<CancellationToken>()).Returns(true);
            Store.TryLockSourceStagedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
            Store.StageNewAsync(Arg.Any<string>(), Arg.Any<DatSubscriptionCheckValues>(), Arg.Any<CancellationToken>()).Returns(call => Stage(call.Arg<DatSubscriptionCheckValues>()));
            Store.StageExistingAsync(Arg.Any<int>(), Arg.Any<DatSubscriptionCheckValues>(), Arg.Any<CancellationToken>()).Returns(call => Stage(call.Arg<DatSubscriptionCheckValues>()));
            Identity.ReadIdAfterFlush().Returns(_ => { Events.Add("subscription-id"); return 1; });
            Reader.ReadWrittenStatusAsync(Identity, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Events.Add("response");
                return Status(Written.ShouldNotBeNull());
            });
            Reader.ReadReplayStatusAsync(Arg.Any<DatSubscriptionCheckSnapshot>(), Arg.Any<CancellationToken>()).Returns(call => Status(call.Arg<DatSubscriptionCheckSnapshot>().Values));
            Client.Enabled.Returns(true);
            Client.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(new[] { new PublishedDatCatalog("catalog", "psx", "Sony", "test", "healthy", "hash", 1, 1, null) });
            Client.FetchAsync("catalog", "psx", "Sony", Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
            var preview = new DatReplacementPreview("", "new-candidate", null, null, false, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, [], false);
            Review.PreviewInitialAsync("Sony", Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(preview);
            var staged = StagedFile;
            Storage.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(staged);
            var publication = Substitute.For<IStagedFilePublication>();
            publication.RequiresFlush.Returns(true);
            publication.ReadAfterFlush().Returns(_ =>
            {
                Events.Add("file-id");
                var file = Romd.Domain.Storage.FileEntity.Rehydrate(55, Romd.Domain.Hashing.Sha256.Parse(new string('a', 64)), 3, 3, false, CheckedAt);
                return new FileStoreResult(file, true, false);
            });
            Storage.PublishStagedAsync(staged, Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("publish"); return publication; });
            Unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Unit.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("flush"); return Task.CompletedTask; });
            Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("commit"); return Task.CompletedTask; });
        }
        private IStagedDatSubscriptionCheck Stage(DatSubscriptionCheckValues values) { Written = values; Events.Add("stage"); return Identity; }
        private static DatCatalogSubscription Status(DatSubscriptionCheckValues values) => new(1, "catalog", values.SystemId,
            values.ExpectedName ?? "catalog", values.PlatformId, null, values.State, values.LastCheckedAt, values.Message,
            values.CandidateSha256, values.JobId, values.NextCheckAt);
        public CreateDatSubscriptionCommandHandler Create() => new(Reader, Store, Dats, Client, Review, Storage, Unit, time, NullLogger<CreateDatSubscriptionCommandHandler>.Instance);
        public CheckDatSubscriptionCommandHandler Check() => new(Reader, Store, Dats, Client, Review, Storage, Unit, time, NullLogger<CheckDatSubscriptionCommandHandler>.Instance);
    }
    private sealed class SequenceClock : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => calls++ == 0 ? CheckedAt : ScheduledAt;
    }
}
