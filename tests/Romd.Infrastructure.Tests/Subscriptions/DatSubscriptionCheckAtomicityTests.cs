using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed partial class DatCatalogEnrollmentServiceTests
{
    private static readonly DateTimeOffset ExactCheckTime = DateTimeOffset.Parse("2026-11-04T12:00:00.123456Z");
    private static readonly DateTimeOffset ExactScheduleTime = ExactCheckTime.AddHours(2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckOwner_ResponseRetainsFullClockPrecision_WhileRowsRetainUnixMilliseconds(bool existing)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database, new CheckSequenceClock());
        await h.InitializeAsync();
        int? id = existing ? (await h.EnrollAsync()).Id : null;
        // Use a fresh clock for the operation under assertion.
        await using var operation = new Harness(database, new CheckSequenceClock());
        await operation.InstallAsync(operation.Client.Bytes);
        var result = id is int current ? (await operation.CheckAsync(current, default)).Value
            : (await operation.CreateAsync(Catalog, null, default)).Value.Subscription;

        result.State.ShouldBe("UpToDate");
        result.AutomaticChecksPaused.ShouldBeFalse();
        result.LastCheckedAt.ShouldBe(ExactCheckTime);
        result.NextCheckAt.ShouldBe(ExactScheduleTime.AddDays(1));
        await using var read = database.CreateContext();
        var row = await read.DatSubscriptions.SingleAsync();
        row.LastCheckedAt.ShouldBe(UnixMs(ExactCheckTime));
        row.NextCheckAt.ShouldBe(UnixMs(ExactScheduleTime.AddDays(1)));
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("source")]
    [InlineData("new")]
    [InlineData("existing")]
    public async Task CheckStore_WithoutTransaction_RejectsBeforeStaging(string operation)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            switch (operation)
            {
                case "catalog":
                    await h.Service.TryLockCatalogStagedAsync(Catalog, default);
                    break;
                case "source":
                    await h.Service.TryLockSourceStagedAsync(7, default);
                    break;
                case "new":
                    await h.Service.StageNewAsync(Catalog, new(), default);
                    break;
                default:
                    await h.Service.StageExistingAsync(7, new(), default);
                    break;
            }
        });
        h.Db.ChangeTracker.Entries().ShouldBeEmpty();
        (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckStore_ReceiptRequiresOwnerFlushAndOriginalTransaction_ForIdentityAndResponse(bool existing, bool unchanged)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        int? existingId = null;
        if (existing)
        {
            await using var seed = await new EfUnitOfWork(h.Db).BeginTransactionAsync();
            DatSubscriptionCheckValues seedValues = unchanged ? new DatSubscriptionCheckValues(LastCheckedAt: ExactCheckTime, NextCheckAt: ExactScheduleTime) : new();
            var write = await h.Service.StageNewAsync(Catalog, seedValues, default);
            await new EfUnitOfWork(h.Db).FlushAsync();
            existingId = write.ReadIdAfterFlush();
            await seed.CommitAsync();
        }
        IStagedDatSubscriptionCheck receipt;
        await using (var transaction = await new EfUnitOfWork(h.Db).BeginTransactionAsync())
        {
            var values = new DatSubscriptionCheckValues(LastCheckedAt: ExactCheckTime, NextCheckAt: ExactScheduleTime);
            receipt = existingId is int id ? await h.Service.StageExistingAsync(id, values, default)
                : await h.Service.StageNewAsync(Catalog, values, default);
            Should.Throw<InvalidOperationException>(() => receipt.ReadIdAfterFlush());
            await Should.ThrowAsync<InvalidOperationException>(() => h.Service.ReadWrittenStatusAsync(receipt, default));
            await new EfUnitOfWork(h.Db).FlushAsync();
            receipt.ReadIdAfterFlush().ShouldBeGreaterThan(0);
            (await h.Service.ReadWrittenStatusAsync(receipt, default)).LastCheckedAt.ShouldBe(ExactCheckTime);
            await transaction.CommitAsync();
        }
        Should.Throw<InvalidOperationException>(() => receipt.ReadIdAfterFlush());
        await Should.ThrowAsync<InvalidOperationException>(() => h.Service.ReadWrittenStatusAsync(receipt, default));
        await using var replacement = await new EfUnitOfWork(h.Db).BeginTransactionAsync();
        Should.Throw<InvalidOperationException>(() => receipt.ReadIdAfterFlush());
        await Should.ThrowAsync<InvalidOperationException>(() => h.Service.ReadWrittenStatusAsync(receipt, default));
        await using var foreign = new Harness(database);
        await Should.ThrowAsync<InvalidOperationException>(() => foreign.Service.ReadWrittenStatusAsync(receipt, default));
    }

    [Fact]
    public async Task CheckStore_StageExisting_DoesNotFlushUnrelatedWork_AndOwnerRollbackRestoresBothRows()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        var target = new Romd.Persistence.Entities.DatSubscriptionEntity { CatalogId = Catalog };
        var unrelated = new Romd.Persistence.Entities.DatSubscriptionEntity { CatalogId = "unrelated" };
        h.Db.DatSubscriptions.AddRange(target, unrelated);
        await h.Db.SaveChangesAsync();
        await using var transaction = await new EfUnitOfWork(h.Db).BeginTransactionAsync();
        unrelated.Message = "unrelated staged write";

        await h.Service.StageExistingAsync(target.Id, new(State: "CheckFailed", Message: "staged check"), default);

        (await h.Db.DatSubscriptions.AsNoTracking().SingleAsync(row => row.Id == target.Id)).State.ShouldBe("NotChecked");
        (await h.Db.DatSubscriptions.AsNoTracking().SingleAsync(row => row.Id == unrelated.Id)).Message.ShouldBeNull();
        await transaction.RollbackAsync();
        h.Db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var read = database.CreateContext();
        (await read.DatSubscriptions.SingleAsync(row => row.Id == target.Id)).State.ShouldBe("NotChecked");
        (await read.DatSubscriptions.SingleAsync(row => row.Id == unrelated.Id)).Message.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckOwner_CleanupFaultAfterRealCommit_ReturnsDurableRepresentation(bool existing)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database, new CheckSequenceClock());
        await h.InitializeAsync();
        int? id = existing ? (await h.EnrollAsync()).Id : null;
        h.UnitOverride = new CheckObservedUnit(new EfUnitOfWork(h.Db), 0, null) { FailCleanupAfterCommit = true };

        var result = id is int current ? (await h.CheckAsync(current, default)).Value
            : (await h.CreateAsync(Catalog, null, default)).Value.Subscription;

        result.State.ShouldBe("ReadyToImport");
        await using var read = database.CreateContext();
        var row = await read.DatSubscriptions.SingleAsync();
        row.Id.ShouldBe(result.Id);
        row.CandidateSha256.ShouldBe(result.CandidateSha256);
        row.CandidateFileId.ShouldNotBeNull();
        (await read.Jobs.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, false)]
    [InlineData(false, 2, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 2, true)]
    public async Task CheckOwner_FaultOrCancellationAfterRealFileOrSubscriptionFlush_RollsBackTogether(bool existing, int atFlush, bool cancel)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        int? id = existing ? (await h.EnrollAsync()).Id : null;
        var before = await h.Db.DatSubscriptions.AsNoTracking().SingleOrDefaultAsync();
        int files = await h.Db.Files.CountAsync();
        h.Client.Bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(h.Client.Bytes).Replace("Test Disc", "New Disc"));
        using var cancellation = new CancellationTokenSource();
        var unit = new CheckObservedUnit(new EfUnitOfWork(h.Db), atFlush, cancel ? cancellation : null);
        h.UnitOverride = unit;

        Exception? failure = null;
        try
        {
            if (id is int current)
                await h.CheckAsync(current, cancellation.Token);
            else
                await h.CreateAsync(Catalog, null, cancellation.Token);
        }
        catch (Exception error) { failure = error; }

        if (cancel)
            failure.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        else
            failure.ShouldBeSameAs(unit.Fault);
        unit.Flushed.ShouldBe(atFlush);
        h.Db.Database.CurrentTransaction.ShouldBeNull();
        h.Db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var read = database.CreateContext();
        (await read.Files.CountAsync()).ShouldBe(files);
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
        var after = await read.DatSubscriptions.SingleOrDefaultAsync();
        if (before is null)
            after.ShouldBeNull();
        else
        {
            after.ShouldNotBeNull().CandidateFileId.ShouldBe(before.CandidateFileId);
            after.CandidateSha256.ShouldBe(before.CandidateSha256);
            after.ActiveSha256.ShouldBe(before.ActiveSha256);
            after.State.ShouldBe(before.State);
            after.NextCheckAt.ShouldBe(before.NextCheckAt);
            after.LastCheckedAt.ShouldBe(before.LastCheckedAt);
            after.ConsecutiveCheckFailures.ShouldBe(before.ConsecutiveCheckFailures);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckOwner_PersistedFailureFlushFault_RollsBackFailureAndBackoff(bool cancel)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var before = await h.EnrollAsync();
        h.Client.Fail = true;
        using var cancellation = new CancellationTokenSource();
        var unit = new CheckObservedUnit(new EfUnitOfWork(h.Db), 1, cancel ? cancellation : null);
        h.UnitOverride = unit;

        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => h.CheckAsync(before.Id, cancellation.Token));
        else
            (await Should.ThrowAsync<IOException>(() => h.CheckAsync(before.Id, cancellation.Token))).ShouldBeSameAs(unit.Fault);

        await using var read = database.CreateContext();
        var after = await read.DatSubscriptions.SingleAsync();
        after.State.ShouldBe(before.State);
        after.CandidateSha256.ShouldBe(before.CandidateSha256);
        after.LastCheckedAt.ShouldBe(UnixMs(before.LastCheckedAt!.Value));
        after.ConsecutiveCheckFailures.ShouldBe(0);
        after.Message.ShouldBeNull();
    }

    [Fact]
    public async Task Create_InFlightFetch_CompetingCreateReturnsBusyWithoutDiscoveryOrFetch()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var first = new Harness(database);
        await first.InitializeAsync();
        await using var contender = new Harness(database);
        first.Client.BeforeFetch = async () =>
        {
            var result = await contender.CreateAsync(Catalog, null, default);
            result.FirstError.Code.ShouldBe("DatEnrollment.Busy");
            contender.Client.Discoveries.ShouldBe(0);
            contender.Client.Fetches.ShouldBe(0);
        };

        (await first.CreateAsync(Catalog, null, default)).IsError.ShouldBeFalse();
        (await first.Db.DatSubscriptions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Check_BoundSourceContended_ReturnsBusyBeforeDiscoveryOrFetch()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var installed = await h.InstallAsync(h.Client.Bytes);
        var row = (await h.CreateAsync(Catalog, installed.DatId, default)).Value.Subscription;
        int fetches = h.Client.Fetches, discoveries = h.Client.Discoveries;
        await using var holder = database.CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(145, {installed.SourceId})");

        (await h.CheckAsync(row.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");

        h.Client.Fetches.ShouldBe(fetches);
        h.Client.Discoveries.ShouldBe(discoveries);
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
    public async Task CheckOwner_FailedOrLostAcknowledgementAndCleanupFault_PreservesCauseWithoutRetry(bool existing, bool afterCommit, bool cancel)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        int? id = existing ? (await h.EnrollAsync()).Id : null;
        var before = await h.Db.DatSubscriptions.AsNoTracking().SingleOrDefaultAsync();
        int files = await h.Db.Files.CountAsync();
        h.Client.Bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(h.Client.Bytes).Replace("Test Disc", "New Disc"));
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("primary check cancellation", cancellation.Token)
            : new IOException("primary check acknowledgement failure");
        var unit = new CheckObservedUnit(new EfUnitOfWork(h.Db), 0, null)
        {
            AcknowledgementFault = cause,
            CommitBeforeFault = afterCommit,
            FailCleanup = true
        };
        h.UnitOverride = unit;

        Exception? error = null;
        try
        {
            if (id is int current)
                await h.CheckAsync(current, cancellation.Token);
            else
                await h.CreateAsync(Catalog, null, cancellation.Token);
        }
        catch (Exception failure) { error = failure; }

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        unit.Begins.ShouldBe(1);
        unit.Commits.ShouldBe(1);
        unit.Disposals.ShouldBe(1);
        h.Db.Database.CurrentTransaction.ShouldBeNull();
        await using var read = database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
        var after = await read.DatSubscriptions.SingleOrDefaultAsync();
        if (afterCommit)
        {
            after.ShouldNotBeNull().State.ShouldBe("ReadyToImport");
            after.CandidateSha256.ShouldNotBe(before?.CandidateSha256);
            after.CandidateFileId.ShouldNotBeNull();
            (await read.Files.CountAsync()).ShouldBe(files + 1);
        }
        else
        {
            h.Db.ChangeTracker.Entries().ShouldBeEmpty();
            (await read.Files.CountAsync()).ShouldBe(files);
            if (before is null)
                after.ShouldBeNull();
            else
            {
                after.ShouldNotBeNull().CandidateFileId.ShouldBe(before.CandidateFileId);
                after.CandidateSha256.ShouldBe(before.CandidateSha256);
                after.State.ShouldBe(before.State);
                after.LastCheckedAt.ShouldBe(before.LastCheckedAt);
                after.NextCheckAt.ShouldBe(before.NextCheckAt);
                after.ConsecutiveCheckFailures.ShouldBe(before.ConsecutiveCheckFailures);
            }
        }
    }

    private static DateTimeOffset UnixMs(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    private sealed class CheckSequenceClock : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => calls++ == 0 ? ExactCheckTime : ExactScheduleTime;
    }
    private sealed class CheckObservedUnit(IUnitOfWork inner, int failAt, CancellationTokenSource? cancellation) : IUnitOfWork
    {
        public IOException Fault { get; } = new("injected check fault after real flush");
        public int Flushed { get; private set; }
        public int Begins { get; private set; }
        public int Commits { get; private set; }
        public int Disposals { get; private set; }
        public bool FailCleanupAfterCommit { get; init; }
        public bool FailCleanup { get; init; }
        public Exception? AcknowledgementFault { get; init; }
        public bool CommitBeforeFault { get; init; }
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
        {
            Begins++;
            return new Transaction(await inner.BeginTransactionAsync(ct), this);
        }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task FlushAsync(CancellationToken ct = default)
        {
            await inner.FlushAsync(ct);
            Flushed++;
            if (Flushed != failAt)
                return;
            if (cancellation is not null)
            { await cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); }
            throw Fault;
        }
        private sealed class Transaction(ITransaction transaction, CheckObservedUnit owner) : ITransaction
        {
            private bool committed;
            public async Task CommitAsync(CancellationToken ct = default)
            {
                owner.Commits++;
                if (owner.AcknowledgementFault is not null)
                {
                    if (owner.CommitBeforeFault)
                        await transaction.CommitAsync(ct);
                    else
                        await owner.FlushAsync(ct);
                    throw owner.AcknowledgementFault;
                }
                await transaction.CommitAsync(ct);
                committed = true;
            }
            public Task RollbackAsync(CancellationToken ct = default) => transaction.RollbackAsync(ct);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                owner.Disposals++;
                if (owner.FailCleanup || (committed && owner.FailCleanupAfterCommit))
                    throw new IOException("injected check cleanup fault");
            }
        }
    }
}
