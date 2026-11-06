using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSubscription;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.Persistence.Subscriptions;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed class DatSubscriptionApplyAtomicityTests : IDisposable
{
    private const string Catalog = "publisher/test/apply-owner";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T03:00:00.123456Z");
    private static readonly DateTimeOffset PersistedNow = DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds());
    private static readonly byte[] Original = Encoding.UTF8.GetBytes(Document("Installed"));
    private static readonly byte[] Candidate = Encoding.UTF8.GetBytes(Document("Reviewed"));
    private static readonly string CandidateHash = Hash(Candidate);
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly string root = Directory.CreateTempSubdirectory("romd-subscription-apply-").FullName;
    private readonly Guid actor = Guid.NewGuid();
    private readonly Client client = new();
    private readonly Dictionary<int, byte[]> documents = [];
    private readonly List<Stream> opened = [];
    private int candidateId;
    private int? sourceId;
    private int? activeId;
    private int candidateReads;
    private bool failFinalDocumentCleanup;
    private string ActiveHash => activeId is null ? "" : Hash(Original);
    private static string Document(string game) => $"<datafile><header><name>Catalog</name><version>1</version></header><game name='{game}'><rom name='one.bin' size='1' crc='11111111'/></game></datafile>";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task InitializeAsync(bool replacement)
    {
        await using var db = database.CreateContext();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Selected",
            ShortName = "selected",
            CanonicalKey = "selected",
            BaseName = "Selected",
            BaseCompactLabel = "Selected",
            Ownership = ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            IsEnabled = false
        });
        db.Users.Add(new RomdUser { Id = actor, UserName = "subscription-approver" });
        var candidate = new FileEntityPersistence { Sha256 = Romd.Domain.Hashing.Sha256.Parse(CandidateHash), Size = Candidate.Length, SizeOnDisk = Candidate.Length };
        db.Files.Add(candidate);
        await db.SaveChangesAsync();
        candidateId = candidate.Id;
        documents[candidate.Id] = Candidate;
        if (replacement)
        {
            var original = new FileEntityPersistence { Sha256 = Romd.Domain.Hashing.Sha256.Parse(Hash(Original)), Size = Original.Length, SizeOnDisk = Original.Length };
            db.Files.Add(original);
            await db.SaveChangesAsync();
            documents[original.Id] = Original;
            var dat = DatFile.CreateNew("Catalog", "Catalog", DatType.Redump, "installed.dat", original.Id, Now, 1);
            await new DatRepository(db, new Clock()).AddStagedAsync(dat, DatSource.CreateNew(Now));
            await db.SaveChangesAsync();
            var installed = await db.DatFiles.SingleAsync();
            sourceId = installed.DatSourceId;
            activeId = installed.Id;
        }
        db.DatSubscriptions.Add(new DatSubscriptionEntity
        {
            Id = 1,
            CatalogId = Catalog,
            SystemId = "selected",
            ExpectedName = "Catalog",
            PlatformId = 1,
            DatSourceId = sourceId,
            State = replacement ? "UpdateAvailable" : "ReadyToImport",
            Message = "review available",
            CandidateFileId = candidateId,
            CandidateSha256 = CandidateHash,
            ActiveSha256 = ActiveHash
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_InitialOrReplacement_CommitsExactJobSubscriptionAndDispatchWithReviewedBytes(bool replacement)
    {
        await InitializeAsync(replacement);
        await using var db = database.CreateContext();
        var work = Work(db);
        work.AfterFlush = async token =>
        {
            (await db.Jobs.CountAsync(token)).ShouldBe(1);
            (await db.JobDispatches.CountAsync(token)).ShouldBe(1);
            (await db.DatSubscriptions.SingleAsync(token)).State.ShouldBe("Applying");
            await AssertUnacceptedAsync(replacement);
        };

        var result = await Handler(db, work).HandleAsync(Command());

        result.IsError.ShouldBeFalse();
        work.Flushed.ShouldBeTrue();
        work.DisposedUnderLease.ShouldBeTrue();
        work.DocumentDisposedUnderLease.ShouldBeTrue();
        await AssertAcceptedAsync(result.Value, replacement);
        await AssertInputAsync(result.Value, replacement);
        opened.ShouldNotBeEmpty();
        opened.ShouldAllBe(stream => !stream.CanRead);
        client.Fetches.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_ActualFlushThenFaultOrCancellation_RollsBackAllRowsAndRetainsInputWithPrimaryThroughCleanup(bool replacement, bool cancel)
    {
        await InitializeAsync(replacement);
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = Work(db);
        work.CleanupFault = true;
        work.AfterFlush = async token =>
        {
            (await db.Jobs.CountAsync(token)).ShouldBe(1);
            (await db.JobDispatches.CountAsync(token)).ShouldBe(1);
            (await db.DatSubscriptions.SingleAsync(token)).State.ShouldBe("Applying");
            await AssertUnacceptedAsync(replacement);
            if (cancel)
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
            throw work.Fault;
        };
        failFinalDocumentCleanup = true;

        var error = await CaptureAsync(() => Handler(db, work).HandleAsync(Command(), cancellation.Token));

        if (cancel)
        {
            var cancelled = error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull();
            cancelled.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            error.ShouldBeSameAs(work.Fault);
        work.Flushed.ShouldBeTrue();
        work.DisposedUnderLease.ShouldBeTrue();
        work.DocumentDisposedUnderLease.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertUnacceptedAsync(replacement);
        await AssertInputAsync(Id(work), replacement);
        opened.ShouldAllBe(stream => !stream.CanRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_CommitAcknowledgementUncertain_RetainsExactInputsWhetherDatabaseCommittedOrRolledBack(bool replacement, bool committed)
    {
        await InitializeAsync(replacement);
        await using var db = database.CreateContext();
        var work = Work(db);
        work.CleanupFault = true;
        if (committed)
            work.AfterCommit = _ => Task.FromException(work.Fault);
        else
            work.AfterFlush = _ => Task.FromException(work.Fault);

        var error = await CaptureAsync(() => Handler(db, work).HandleAsync(Command()));

        error.ShouldBeSameAs(work.Fault);
        work.Committed.ShouldBe(committed);
        work.DisposedUnderLease.ShouldBeTrue();
        work.DocumentDisposedUnderLease.ShouldBeTrue();
        if (committed)
            await AssertAcceptedAsync(Id(work), replacement);
        else
            await AssertUnacceptedAsync(replacement);
        await AssertInputAsync(Id(work), replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AcknowledgedCommitAndCleanupFaults_ReturnsDurableIdentityAndDisabledReplayPreservesPinsAndInputs(bool replacement)
    {
        await InitializeAsync(replacement);
        await using var db = database.CreateContext();
        var work = Work(db);
        work.CleanupFault = true;
        failFinalDocumentCleanup = true;

        var accepted = await Handler(db, work).HandleAsync(Command());

        accepted.IsError.ShouldBeFalse();
        work.DocumentDisposedUnderLease.ShouldBeTrue();
        await AssertAcceptedAsync(accepted.Value, replacement);
        int reads = candidateReads;
        client.Enabled = false;
        var replay = await Handler(db, Work(db)).HandleAsync(Command());
        replay.Value.ShouldBe(accepted.Value);
        (await Handler(db, Work(db)).HandleAsync(Command() with { CandidateSha256 = CandidateHash.ToUpperInvariant() }))
            .FirstError.ShouldBe(DatEnrollmentErrors.Stale());
        candidateReads.ShouldBe(reads);
        await AssertInputAsync(accepted.Value, replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CatalogOrSourceHeldWhileDisabled_BusyPrecedesStaleAndUnavailableWithoutChangingState(bool source)
    {
        await InitializeAsync(replacement: true);
        await using var holder = database.CreateContext();
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync();
        if (source)
            await holder.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(145, {sourceId!.Value})");
        else
            await holder.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(146, hashtext({Catalog}))");
        await using var db = database.CreateContext();
        client.Enabled = false;

        var busy = await Handler(db, Work(db)).HandleAsync(Command() with { CandidateSha256 = new string('0', 64) });

        busy.FirstError.ShouldBe(DatEnrollmentErrors.Busy());
        candidateReads.ShouldBe(0);
        await AssertUnacceptedAsync(replacement: true);
        await transaction.RollbackAsync();
        (await Handler(db, Work(db)).HandleAsync(Command() with { CandidateSha256 = new string('0', 64) }))
            .FirstError.ShouldBe(DatEnrollmentErrors.Stale());
        (await Handler(db, Work(db)).HandleAsync(Command())).FirstError.ShouldBe(DatEnrollmentErrors.Unavailable());
        await AssertUnacceptedAsync(replacement: true);
    }

    private ApplyDatSubscriptionCommand Command() => new(1, ActiveHash, CandidateHash);
    private ObservedWork Work(RomdDbContext db) => new(new EfUnitOfWork(db), root);
    private static Guid Id(ObservedWork work)
    {
        work.JobId.HasValue.ShouldBeTrue();
        return work.JobId.GetValueOrDefault();
    }

    private ApplyDatSubscriptionCommandHandler Handler(RomdDbContext db, ObservedWork work)
    {
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            int id = call.Arg<int>();
            bool fail = id == candidateId && ++candidateReads == 2 && failFinalDocumentCleanup;
            Stream stream = new DocumentStream(documents[id], fail, work.ObserveDocumentDisposal);
            opened.Add(stream);
            return Task.FromResult<Stream?>(stream);
        });
        var dats = new DatRepository(db, new Clock());
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        var review = new DatReplacementReview(dats, storage, parser, parser);
        var service = new DatCatalogEnrollmentService(db, client, dats, storage, review, new Clock());
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(root);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actor);
        return new(service, service, service, dats, client, storage, review, review, new JobRepository(db, new Clock()),
            new ObservedExclusion(new JobWorkspaceExclusion(options), work), new UploadWorkspacePreparation(options),
            new ReplaceDatWorkspacePreparation(options), new UploadJobRepository(db, new Clock()),
            new ReplaceDatJobRepository(db, new Clock()), work, user, new Clock(), NullLogger<ApplyDatSubscriptionCommandHandler>.Instance);
    }

    private async Task AssertUnacceptedAsync(bool replacement)
    {
        await using var read = database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
        var row = await read.DatSubscriptions.SingleAsync();
        row.State.ShouldBe(replacement ? "UpdateAvailable" : "ReadyToImport");
        row.JobId.ShouldBeNull();
        row.DatSourceId.ShouldBe(sourceId);
        row.ActiveSha256.ShouldBe(ActiveHash);
        row.CandidateSha256.ShouldBe(CandidateHash);
        row.CandidateFileId.ShouldBe(candidateId);
        row.Message.ShouldBe("review available");
    }

    private async Task AssertAcceptedAsync(Guid id, bool replacement)
    {
        await using var read = database.CreateContext();
        var job = (await read.Jobs.ToListAsync()).ShouldHaveSingleItem();
        job.Id.ShouldBe(id);
        id.ShouldNotBe(Guid.Empty);
        job.CorrelationId.ShouldNotBe(Guid.Empty);
        job.SourceFilename.ShouldBe(replacement ? "reviewed-catalog.dat" : "subscribed-catalog.dat");
        job.PlatformId.ShouldBe(replacement ? null : 1);
        job.CreatedByUserId.ShouldBe(replacement ? null : actor);
        job.CreatedAt.ShouldBe(PersistedNow);
        job.Phase.ShouldBe("Pending");
        if (replacement)
            (await read.Jobs.OfType<ReplaceDatJobEntity>().SingleAsync()).ExistingDatId.ShouldBe(activeId.GetValueOrDefault());
        else
        {
            var upload = await read.Jobs.OfType<UploadJobEntity>().SingleAsync();
            upload.MaxParallelRoms.ShouldBe(4);
            upload.AllowUnidentified.ShouldBeFalse();
            upload.ArchiveOnly.ShouldBeFalse();
            upload.TrackedOnly.ShouldBeFalse();
        }
        var row = await read.DatSubscriptions.SingleAsync();
        row.JobId.ShouldBe(id);
        row.DatSourceId.ShouldBe(sourceId);
        row.State.ShouldBe("Applying");
        row.Message.ShouldBeNull();
        row.ActiveSha256.ShouldBe(ActiveHash);
        row.CandidateSha256.ShouldBe(CandidateHash);
        row.CandidateFileId.ShouldBe(candidateId);
        var dispatch = (await read.JobDispatches.ToListAsync()).ShouldHaveSingleItem();
        dispatch.JobId.ShouldBe(id);
        dispatch.JobType.ShouldBe(replacement ? "replace_dat" : "upload");
        dispatch.CreatedAtUtc.ShouldBe(PersistedNow);
        dispatch.AvailableAtUtc.ShouldBe(PersistedNow);
        (await read.Platforms.SingleAsync()).IsEnabled.ShouldBe(false);
    }

    private async Task AssertInputAsync(Guid id, bool replacement)
    {
        string name = replacement ? "reviewed-catalog.dat" : "subscribed-catalog.dat";
        (await File.ReadAllBytesAsync(Path.Combine(root, "temp", "jobs", $"{id:N}", name))).ShouldBe(Candidate);
        using var successor = JobWorkspace.TryAcquireExecutionLock(root, id).ShouldNotBeNull();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }
    public void Dispose() { database.Dispose(); Directory.Delete(root, recursive: true); }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Client : ISignedDatCatalogClient
    {
        public bool Enabled { get; set; } = true;
        public int Fetches { get; private set; }
        public Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct) => throw new InvalidOperationException("Apply must use its saved candidate.");
        public Task<ErrorOr<byte[]>> FetchAsync(string catalogId, string systemId, string expectedName, CancellationToken ct)
        { Fetches++; throw new InvalidOperationException("Apply must not download another candidate."); }
    }
    private sealed class DocumentStream(byte[] bytes, bool failCleanup, Action observeDisposal) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask DisposeAsync()
        {
            observeDisposal();
            await base.DisposeAsync();
            if (failCleanup)
                throw new IOException("candidate document cleanup failed");
        }
    }
    private sealed class ObservedExclusion(IJobWorkspaceExclusion inner, ObservedWork work) : IJobWorkspaceExclusion
    {
        public Task<IJobWorkspaceLease> AcquireAsync(Guid jobId, CancellationToken ct = default)
        { work.JobId = jobId; return inner.AcquireAsync(jobId, ct); }
    }
    private sealed class ObservedWork(IUnitOfWork inner, string root) : IUnitOfWork
    {
        public Guid? JobId { get; set; }
        public bool Flushed { get; private set; }
        public bool Committed { get; private set; }
        public bool DisposedUnderLease { get; private set; }
        public bool DocumentDisposedUnderLease { get; private set; }
        public bool CleanupFault { get; set; }
        public IOException Fault { get; } = new("subscription acceptance failure after actual SQL");
        public Func<CancellationToken, Task>? AfterFlush { get; set; }
        public Func<CancellationToken, Task>? AfterCommit { get; set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task FlushAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Apply needs no intermediate identity flush.");
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
        private void AssertLeaseHeld()
        {
            if (JobId is not { } id)
                return;
            using var contender = JobWorkspace.TryAcquireExecutionLock(root, id);
            contender.ShouldBeNull();
        }
        public void ObserveDocumentDisposal()
        {
            if (JobId is null)
                return;
            DisposedUnderLease.ShouldBeTrue();
            AssertLeaseHeld();
            DocumentDisposedUnderLease = true;
        }
        private async Task CommitAsync(ITransaction transaction, CancellationToken ct)
        {
            AssertLeaseHeld();
            await inner.FlushAsync(ct);
            Flushed = true;
            if (AfterFlush is not null)
                await AfterFlush(ct);
            await transaction.CommitAsync(ct);
            Committed = true;
            AssertLeaseHeld();
            if (AfterCommit is not null)
                await AfterCommit(ct);
        }
        private async ValueTask DisposeAsync(ITransaction transaction)
        {
            AssertLeaseHeld();
            await transaction.DisposeAsync();
            AssertLeaseHeld();
            DisposedUnderLease = JobId is not null;
            if (CleanupFault)
                throw new IOException("subscription transaction cleanup failed");
        }
        private sealed class Transaction(ITransaction transaction, ObservedWork owner) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => owner.CommitAsync(transaction, cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => owner.DisposeAsync(transaction);
        }
    }
}
