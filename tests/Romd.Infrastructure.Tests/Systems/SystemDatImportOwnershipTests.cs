using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform.Commands.ImportSystemDat;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.Persistence.Systems;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Systems;

public sealed class SystemDatImportOwnershipTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T01:00:00.123456Z");
    private static readonly DateTimeOffset PersistedNow = DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds());
    private static readonly byte[] Document = Encoding.UTF8.GetBytes(
        "<datafile><header><name>System import</name><version>1</version></header><game name='Nonempty'><rom name='one.bin' size='1' crc='11111111'/></game></datafile>");
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Document));
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly string root = Directory.CreateTempSubdirectory("romd-system-import-owner-").FullName;
    private readonly Guid actor = Guid.NewGuid();

    public SystemDatImportOwnershipTests()
    {
        using var db = database.CreateContext();
        db.Platforms.AddRange(new PlatformEntity { Id = 1, Name = "Selected", ShortName = "selected", IsEnabled = false },
            new PlatformEntity { Id = 2, Name = "Other", ShortName = "other", IsEnabled = false });
        db.Users.Add(new RomdUser { Id = actor, UserName = "system-importer" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_WithoutCallerTransaction_RejectsWithoutEnablingOrTracking(bool installedRead)
    {
        await using var db = database.CreateContext();
        var store = new SystemDatImportStore(db);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => installedRead
            ? store.IsDocumentInstalledAsync(Sha256.Parse(Hash)) : store.EnableStagedAsync(1));

        error.Message.ShouldBe("System DAT import requires a caller-owned transaction.");
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertUnacceptedAsync();
    }

    [Fact]
    public async Task Handle_NonemptyDocument_CommitsEnablementExactUploadAndDispatchTogether()
    {
        await using var db = database.CreateContext();
        var work = Work(db);
        work.AfterFlush = async _ =>
        {
            (await db.Platforms.SingleAsync(platform => platform.Id == 1)).IsEnabled.ShouldBe(true);
            (await db.Jobs.CountAsync()).ShouldBe(1);
            (await db.JobDispatches.CountAsync()).ShouldBe(1);
            await AssertUnacceptedAsync();
        };
        using var input = Input();

        var result = await Handler(db, work).HandleAsync(new(1, input, Hash));

        result.IsError.ShouldBeFalse();
        work.Flushed.ShouldBeTrue();
        work.Committed.ShouldBeTrue();
        work.Disposed.ShouldBeTrue();
        await AssertAcceptedAsync(result.Value);
        await AssertRetainedInputAsync(result.Value);
        input.CanRead.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_FaultOrCancellationAfterActualSqlFlush_RollsBackAllDatabaseEffectsAndRetainsInput(bool cancel, bool cleanupFault)
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = Work(db);
        var cause = new IOException("after actual System Import flush");
        work.CleanupFault = cleanupFault;
        work.AfterFlush = async token =>
        {
            (await db.Jobs.CountAsync(token)).ShouldBe(1);
            (await db.JobDispatches.CountAsync(token)).ShouldBe(1);
            (await db.Platforms.SingleAsync(platform => platform.Id == 1, token)).IsEnabled.ShouldBe(true);
            await AssertUnacceptedAsync();
            if (cancel)
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
            throw cause;
        };
        using var input = Input();

        var error = await CaptureAsync(() => Handler(db, work).HandleAsync(new(1, input, Hash), cancellation.Token));

        if (cancel)
        {
            var cancelled = error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull();
            cancelled.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            error.ShouldBeSameAs(cause);
        work.Flushed.ShouldBeTrue();
        work.Committed.ShouldBeFalse();
        work.Disposed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertUnacceptedAsync();
        work.JobId.HasValue.ShouldBeTrue();
        await AssertRetainedInputAsync(work.JobId.GetValueOrDefault());
        input.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CommitAcknowledgementUncertain_RetainsCompletedInputAndOriginalExceptionRegardlessOfLaterDatabaseRead(bool databaseCommitted)
    {
        await using var db = database.CreateContext();
        var work = Work(db);
        var cause = new IOException("uncertain System Import acknowledgement");
        work.CleanupFault = true;
        if (databaseCommitted)
            work.AfterCommit = _ => Task.FromException(cause);
        else
            work.BeforeCommit = _ => Task.FromException(cause);
        using var input = Input();

        var error = await CaptureAsync(() => Handler(db, work).HandleAsync(new(1, input, Hash)));

        error.ShouldBeSameAs(cause);
        work.JobId.HasValue.ShouldBeTrue();
        var id = work.JobId.GetValueOrDefault();
        work.CommitAttempted.ShouldBeTrue();
        work.Committed.ShouldBe(databaseCommitted);
        work.Disposed.ShouldBeTrue();
        if (databaseCommitted)
            await AssertAcceptedAsync(id);
        else
            await AssertUnacceptedAsync();
        await AssertRetainedInputAsync(id);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_CleanupFailureAfterAcknowledgedCommit_ReturnsDurableIdentityAndRetainsInput()
    {
        await using var db = database.CreateContext();
        var work = Work(db);
        work.CleanupFault = true;
        using var input = Input();

        var result = await Handler(db, work).HandleAsync(new(1, input, Hash));

        result.IsError.ShouldBeFalse();
        work.Committed.ShouldBeTrue();
        work.Disposed.ShouldBeTrue();
        await AssertAcceptedAsync(result.Value);
        await AssertRetainedInputAsync(result.Value);
    }

    [Fact]
    public async Task Handle_ConcurrentImports_SerializeOnExistingPlatformRowAndAcceptFreshJobsWithoutInventingDocumentDeduplication()
    {
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWork = Work(firstDb);
        firstWork.AfterFlush = async token =>
        {
            flushed.SetResult();
            await release.Task.WaitAsync(token);
        };
        using var firstInput = Input();
        using var secondInput = Input();
        var first = Handler(firstDb, firstWork).HandleAsync(new(1, firstInput, Hash), cancellation.Token);
        Task<ErrorOr<Guid>>? second = null;
        Exception? failure = null;
        try
        {
            await Task.WhenAny(flushed.Task, first).WaitAsync(cancellation.Token);
            if (!flushed.Task.IsCompletedSuccessfully)
                await first;
            flushed.Task.IsCompletedSuccessfully.ShouldBeTrue();
            var observed = await database.StartWaitingForALockAsync(secondDb,
                token => Handler(secondDb, Work(secondDb)).HandleAsync(new(1, secondInput, Hash), token), cancellation.Token);
            second = observed.Pending;
            observed.Waited.ShouldBeTrue();
            await AssertUnacceptedAsync();
            release.SetResult();
            var firstResult = await first;
            var secondResult = await second;
            firstResult.IsError.ShouldBeFalse();
            secondResult.IsError.ShouldBeFalse();
            firstResult.Value.ShouldNotBe(secondResult.Value);
            await using var read = database.CreateContext();
            (await read.Platforms.SingleAsync(platform => platform.Id == 1)).IsEnabled.ShouldBe(true);
            var jobs = await read.Jobs.OfType<UploadJobEntity>().ToListAsync();
            jobs.Count.ShouldBe(2);
            jobs.Select(job => job.Id).ShouldBe([firstResult.Value, secondResult.Value], ignoreOrder: true);
            jobs.Select(job => job.CorrelationId).Distinct().Count().ShouldBe(2);
            (await read.JobDispatches.Select(dispatch => dispatch.JobId).ToListAsync())
                .ShouldBe([firstResult.Value, secondResult.Value], ignoreOrder: true);
            await AssertRetainedInputAsync(firstResult.Value);
            await AssertRetainedInputAsync(secondResult.Value);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            release.TrySetResult();
            await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure,
                second is null ? [first] : [first, second]);
        }
    }

    [Theory]
    [InlineData(nameof(DatFileLifecycle.Active))]
    [InlineData(nameof(DatFileLifecycle.PendingActivation))]
    [InlineData(nameof(DatFileLifecycle.Superseded))]
    public async Task Handle_DocumentInstalledOnOtherQuarantinedPlatform_RejectsEveryLifecycleGloballyAndRollsBackEnablementBeforeCopy(string lifecycle)
    {
        await using var db = database.CreateContext();
        var file = new FileEntityPersistence { Sha256 = Sha256.Parse(Hash), Size = Document.Length, SizeOnDisk = Document.Length };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        var dat = DatFile.CreateNew("Other catalog", "Other catalog", DatType.Redump, "other.dat", file.Id, Now, platformId: 2);
        await new DatRepository(db, new FixedClock()).AddStagedAsync(dat, DatSource.CreateNew(Now));
        await db.SaveChangesAsync();
        await db.DatFiles.ExecuteUpdateAsync(setters => setters.SetProperty(version => version.Lifecycle, lifecycle));
        await db.CatalogSources.ExecuteUpdateAsync(setters => setters.SetProperty(source => source.Status, "Quarantined"));
        var work = Work(db);
        using var input = Input();

        var result = await Handler(db, work).HandleAsync(new(1, input, Hash));

        result.FirstError.Code.ShouldBe("SystemSetup.Unchanged");
        work.JobId.ShouldBeNull();
        work.Flushed.ShouldBeFalse();
        work.CommitAttempted.ShouldBeFalse();
        work.Disposed.ShouldBeTrue();
        await AssertUnacceptedAsync();
        Directory.Exists(Path.Combine(root, "temp", "jobs")).ShouldBeFalse();
        var retained = await db.DatFiles.SingleAsync();
        retained.FileId.ShouldBe(file.Id);
        retained.Lifecycle.ShouldBe(lifecycle);
    }

    private ObservedWork Work(RomdDbContext db) => new(new EfUnitOfWork(db), root);

    private ImportSystemDatCommandHandler Handler(RomdDbContext db, ObservedWork work)
    {
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        var review = new DatReplacementReview(new DatRepository(db, new FixedClock()), Substitute.For<IFileStorageService>(),
            parser, parser);
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(root);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actor);
        return new(review, new SystemDatImportStore(db), new JobRepository(db, new FixedClock()),
            new JobWorkspaceExclusion(options), new ObservedPreparation(new UploadWorkspacePreparation(options), work),
            new UploadJobRepository(db, new FixedClock()), work, user, new FixedClock(), NullLogger<ImportSystemDatCommandHandler>.Instance);
    }

    private async Task AssertUnacceptedAsync()
    {
        await using var read = database.CreateContext();
        (await read.Platforms.SingleAsync(platform => platform.Id == 1)).IsEnabled.ShouldBe(false);
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }

    private async Task AssertAcceptedAsync(Guid id)
    {
        await using var read = database.CreateContext();
        (await read.Platforms.SingleAsync(platform => platform.Id == 1)).IsEnabled.ShouldBe(true);
        (await read.Platforms.SingleAsync(platform => platform.Id == 2)).IsEnabled.ShouldBe(false);
        var job = (await read.Jobs.OfType<UploadJobEntity>().ToListAsync()).ShouldHaveSingleItem();
        job.Id.ShouldBe(id);
        id.ShouldNotBe(Guid.Empty);
        job.CorrelationId.ShouldNotBe(Guid.Empty);
        job.SourceFilename.ShouldBe("reviewed-catalog.dat");
        job.PlatformId.ShouldBe(1);
        job.CreatedByUserId.ShouldBe(actor);
        job.CreatedAt.ShouldBe(PersistedNow);
        job.Phase.ShouldBe("Pending");
        job.MaxParallelRoms.ShouldBe(4);
        job.AllowUnidentified.ShouldBeFalse();
        job.ArchiveOnly.ShouldBeFalse();
        job.TrackedOnly.ShouldBeFalse();
        var dispatch = (await read.JobDispatches.ToListAsync()).ShouldHaveSingleItem();
        dispatch.JobId.ShouldBe(id);
        dispatch.JobType.ShouldBe("upload");
        dispatch.CreatedAtUtc.ShouldBe(PersistedNow);
        dispatch.AvailableAtUtc.ShouldBe(PersistedNow);
    }

    private async Task AssertRetainedInputAsync(Guid id)
    {
        (await File.ReadAllBytesAsync(Path.Combine(root, "temp", "jobs", $"{id:N}", "reviewed-catalog.dat"))).ShouldBe(Document);
        using var nextOwner = JobWorkspace.TryAcquireExecutionLock(root, id).ShouldNotBeNull();
    }

    private static MemoryStream Input() => new(Document, writable: false);
    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    public void Dispose()
    {
        database.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ObservedPreparation(IUploadWorkspacePreparation inner, ObservedWork work) : IUploadWorkspacePreparation
    {
        public async Task<ErrorOr<IPreparedJobWorkspace>> PrepareAsync(JobWorkspaceInput input, Stream source,
            IJobWorkspaceLease lease, CancellationToken ct = default)
        {
            work.JobId = input.JobId;
            return await inner.PrepareAsync(input, source, lease, ct);
        }
    }

    private sealed class ObservedWork(IUnitOfWork inner, string root) : IUnitOfWork
    {
        public Guid? JobId { get; set; }
        public bool Flushed { get; private set; }
        public bool CommitAttempted { get; private set; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public bool CleanupFault { get; set; }
        public Func<CancellationToken, Task>? AfterFlush { get; set; }
        public Func<CancellationToken, Task>? BeforeCommit { get; set; }
        public Func<CancellationToken, Task>? AfterCommit { get; set; }

        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            Flushed = true;
            AssertLeaseHeld();
            if (AfterFlush is not null)
                await AfterFlush(cancellationToken);
        }

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new ObservedTransaction(await inner.BeginTransactionAsync(cancellationToken), this);

        private void AssertLeaseHeld()
        {
            if (JobId is { } id)
            {
                using var contender = JobWorkspace.TryAcquireExecutionLock(root, id);
                contender.ShouldBeNull();
            }
        }

        private sealed class ObservedTransaction(ITransaction transaction, ObservedWork owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                owner.CommitAttempted = true;
                owner.AssertLeaseHeld();
                if (owner.BeforeCommit is not null)
                    await owner.BeforeCommit(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                owner.Committed = true;
                owner.AssertLeaseHeld();
                if (owner.AfterCommit is not null)
                    await owner.AfterCommit(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);

            public async ValueTask DisposeAsync()
            {
                owner.AssertLeaseHeld();
                await transaction.DisposeAsync();
                owner.Disposed = true;
                owner.AssertLeaseHeld();
                if (owner.CleanupFault)
                    throw new IOException("System Import transaction cleanup failed");
            }
        }
    }
}
