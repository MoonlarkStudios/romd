using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class OriginalDiscLayoutTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Intake_IdentityOrCommitInterrupted_RollsBackFileLayoutAuditAndOutcome(bool beforeCommit, bool cancel)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-descriptor-staging", Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = root).BuildServiceProvider();
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            await using var db = database.CreateContext();
            var claim = await ClaimDescriptorUploadAsync(db);
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            using var cancellation = new CancellationTokenSource();
            var work = new InterruptDescriptorIntake(new EfUnitOfWork(db), db, beforeCommit, cancel, cancellation);
            var storage = DescriptorStorage(db, cas);
            var intake = new OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)), storage,
                new JobItemRepository(db), work, MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), TimeProvider.System);
            byte[] cue = Encoding.UTF8.GetBytes("REM new source\nFILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n");
            await using var bytes = new MemoryStream(cue);

            if (cancel)
                (await Should.ThrowAsync<OperationCanceledException>(() => intake.IntakeAsync(bytes, claim.Job.Id, "disc.cue", cancellation.Token)))
                    .CancellationToken.ShouldBe(cancellation.Token);
            else
                await Should.ThrowAsync<IOException>(() => intake.IntakeAsync(bytes, claim.Job.Id, "disc.cue", cancellation.Token));

            work.GeneratedFileId.ShouldBeGreaterThan(0);
            work.SawLayoutAuditAndOutcome.ShouldBe(beforeCommit);
            db.ChangeTracker.Entries().ShouldBeEmpty();
            await db.SaveChangesAsync();
            await using var verification = database.CreateContext();
            (await verification.Files.CountAsync()).ShouldBe(3);
            (await verification.OriginalDiscLayouts.CountAsync()).ShouldBe(0);
            (await verification.Set<OriginalDiscLayoutAuditEntity>().CountAsync()).ShouldBe(0);
            (await verification.JobItems.CountAsync()).ShouldBe(0);
            var hash = Romd.Domain.Hashing.Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(cue));
            (await cas.ExistsAsync(StorageKey.FromHash(hash))).ShouldBeTrue();
            (await storage.DeleteOrphanAsync(hash, default)).ShouldBeTrue();
            (await cas.ExistsAsync(StorageKey.FromHash(hash))).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Intake_ConcurrentSameMember_FencedOwnersReturnOneCommittedDescriptorAndOutcome()
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-descriptor-race", Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = root).BuildServiceProvider();
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            await using var seed = database.CreateContext();
            var claim = await ClaimDescriptorUploadAsync(seed);
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int ready = 0;
            async Task WaitAsync(CancellationToken ct)
            {
                if (Interlocked.Increment(ref ready) == 2)
                    both.TrySetResult();
                await both.Task.WaitAsync(ct);
            }
            async Task<JobItem> IntakeAsync()
            {
                await using var db = database.CreateContext();
                var work = new BeginDescriptorTogether(new EfUnitOfWork(db), WaitAsync);
                var intake = new OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)), DescriptorStorage(db, cas),
                    new JobItemRepository(db), work, MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), TimeProvider.System);
                await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("REM concurrent\nFILE \"track.bin\" BINARY\n"));
                return await intake.IntakeAsync(bytes, claim.Job.Id, "disc.cue", cancellation.Token);
            }

            var outcomes = await Task.WhenAll(IntakeAsync(), IntakeAsync());

            outcomes[0].Id.ShouldBe(outcomes[1].Id);
            await using var verification = database.CreateContext();
            var layout = await verification.OriginalDiscLayouts.SingleAsync();
            (await verification.Files.CountAsync()).ShouldBe(4);
            (await verification.Set<OriginalDiscLayoutAuditEntity>().CountAsync()).ShouldBe(1);
            (await verification.JobItems.SingleAsync()).Id.ShouldBe(outcomes[0].Id);
            layout.SourceJobId.ShouldBe(claim.Job.Id);
            (await cas.ExistsAsync(StorageKey.FromHash((await verification.Files.SingleAsync(row => row.Id == layout.DescriptorFileId)).Sha256))).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static FileStorageService DescriptorStorage(RomdDbContext db, IContentAddressableStore cas) => new(cas,
        new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db), new AdminRealtimeOutbox(db, TimeProvider.System),
        NullLogger<FileStorageService>.Instance, TimeProvider.System);

    private static async Task<JobExecutionClaim<UploadJob>> ClaimDescriptorUploadAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var upload = UploadJob.Create("descriptors", createdAt: now);
        upload.Start("descriptor-fixture", startedAt: now);
        upload.BeginClassification();
        upload.SetDiscoveredFiles(0, 2);
        upload.BeginDatIngestion();
        upload.BeginRomIngestion();
        var repository = new UploadJobRepository(db, TimeProvider.System);
        await using (var fixtureAcceptance = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await repository.AddStagedAsync(upload);
            await fixtureAcceptance.CommitAsync();
        }
        db.ChangeTracker.Clear();
        return (await new ClaimedJobRepository<UploadJob, UploadJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(upload.Id, "descriptor-fixture")).ShouldNotBeNull();
    }

    private sealed class BeginDescriptorTogether(IUnitOfWork inner, Func<CancellationToken, Task> ready) : IUnitOfWork
    {
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            await ready(cancellationToken);
            return await inner.BeginTransactionAsync(cancellationToken);
        }
    }

    private sealed class InterruptDescriptorIntake(IUnitOfWork inner, RomdDbContext db, bool beforeCommit, bool cancel,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public int GeneratedFileId { get; private set; }
        public bool SawLayoutAuditAndOutcome { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            var pending = db.ChangeTracker.Entries<FileEntityPersistence>().Where(entry => entry.State == EntityState.Added)
                .Select(entry => entry.Entity).ToArray();
            await inner.FlushAsync(cancellationToken);
            if (pending.Length == 0)
                return;
            GeneratedFileId = pending.ShouldHaveSingleItem().Id;
            (await db.Files.AnyAsync(row => row.Id == GeneratedFileId, cancellationToken)).ShouldBeTrue();
            if (!beforeCommit)
                await InterruptAsync(cancellationToken);
        }
        private async Task BeforeCommitAsync(CancellationToken ct)
        {
            if (!beforeCommit)
                return;
            await inner.FlushAsync(ct);
            SawLayoutAuditAndOutcome = await db.OriginalDiscLayouts.AnyAsync(ct) && await db.Set<OriginalDiscLayoutAuditEntity>().AnyAsync(ct)
                && await db.JobItems.AnyAsync(ct);
            await InterruptAsync(ct);
        }
        private async Task InterruptAsync(CancellationToken ct)
        {
            if (cancel)
            {
                await cancellation.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected descriptor publication interruption.");
        }
        private sealed class Transaction(InterruptDescriptorIntake owner, ITransaction inner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.BeforeCommitAsync(cancellationToken);
                await inner.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
