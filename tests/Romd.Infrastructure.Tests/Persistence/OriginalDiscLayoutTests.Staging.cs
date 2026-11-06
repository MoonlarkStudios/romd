using System.Text;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class OriginalDiscLayoutTests
{
    [Fact]
    public async Task RecordStaged_PendingDuplicate_KeepsFirstDescriptorAndAuditWithoutFlushingUnrelatedState()
    {
        await using var db = database.CreateContext();
        var now = DateTimeOffset.UnixEpoch.AddTicks(1234567);
        var service = new OriginalDiscLayoutService(db, new FileMutationLock(db), new ManualTimeProvider(now));
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var platform = await db.Platforms.AsTracking().SingleAsync(row => row.Id == 1);
        platform.Name = "Still staged";
        var parsed = OriginalCueDescriptor.Parse(Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY"), "disc.cue");

        await service.RecordStagedAsync(job, "disc.cue", "disc.cue", 2, parsed, default);
        await service.RecordStagedAsync(job, "disc.cue", "different.cue", 3, new([], "Different"), default);

        (await db.OriginalDiscLayouts.CountAsync()).ShouldBe(0);
        (await db.Platforms.SingleAsync(row => row.Id == 1)).Name.ShouldBe("PlayStation");
        db.ChangeTracker.Entries<OriginalDiscLayoutEntity>().ShouldHaveSingleItem();
        db.ChangeTracker.Entries<OriginalDiscLayoutAuditEntity>().ShouldHaveSingleItem();
        await work.FlushAsync();
        var row = await db.OriginalDiscLayouts.Include(value => value.Audit).SingleAsync();
        row.Id.ShouldBeGreaterThan(0);
        row.DescriptorFileId.ShouldBe(2);
        row.EntryPoint.ShouldBe("disc.cue");
        row.Audit.ShouldHaveSingleItem().At.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
        db.Entry(platform).State.ShouldBe(EntityState.Unchanged);
        platform.Name = "After flush";
        db.Entry(platform).State = EntityState.Modified;
        await service.RecordStagedAsync(job, "disc.cue", "ignored.cue", 3, new([], "Different"), default);
        (await db.Platforms.SingleAsync(value => value.Id == 1)).Name.ShouldBe("Still staged");
        await transaction.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await work.FlushAsync();
        await using var verification = database.CreateContext();
        (await verification.OriginalDiscLayouts.CountAsync()).ShouldBe(0);
        (await verification.Platforms.SingleAsync(value => value.Id == 1)).Name.ShouldBe("PlayStation");
    }

    [Fact]
    public async Task DescriptorStaging_RequiresTransactionAndPropagatesCancellationBeforeTracking()
    {
        await using var db = database.CreateContext();
        var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));
        var parsed = new OriginalCueDescriptor([], null);
        var file = await db.Files.SingleAsync(row => row.Id == 2);
        await Should.ThrowAsync<InvalidOperationException>(() => service.RecordStagedAsync(job, "disc.cue", "disc.cue", 2, parsed, default));
        await Should.ThrowAsync<InvalidOperationException>(() => service.RecordMatchedStagedAsync(job, new("disc.cue", 2,
            file.Sha256, file.Size), parsed, default));
        await Should.ThrowAsync<InvalidOperationException>(() => service.ReconcileStagedAsync(job, default));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        (await Should.ThrowAsync<OperationCanceledException>(() => service.RecordStagedAsync(job, "disc.cue", "disc.cue", 2, parsed, cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconcileStaged_RepeatBeforeFlush_KeepsOnePendingBindingAndLeavesPersistenceToOwner()
    {
        int layoutId = Decode((await Intake()).Id);
        await using var db = database.CreateContext();
        var upload = await SeedSchedulingUploadAsync(db, [layoutId]);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));

        (await service.ReconcileStagedAsync(upload.Id, default)).ShouldBe([layoutId]);
        (await service.ReconcileStagedAsync(upload.Id, default)).ShouldBeEmpty();

        (await db.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
        db.ChangeTracker.Entries<OriginalDiscLayoutFileEntity>().Count(entry => entry.State == EntityState.Added).ShouldBe(1);
        await work.FlushAsync();
        (await db.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(1);
        await using (var observer = database.CreateContext())
            (await observer.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await work.FlushAsync();
        (await db.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_BindingFlushInterrupted_RollsBackBindingsAndPreservesCommittedSiblingProgress(bool cancel)
    {
        int layoutId = Decode((await Intake()).Id);
        await using var db = database.CreateContext();
        var upload = await SeedSchedulingUploadAsync(db, [layoutId]);
        // The setup has committed; the execution claim owns a fresh tracking scope.
        db.ChangeTracker.Clear();
        var inner = new UploadJobRepository(db, TimeProvider.System);
        var claim = (await new ClaimedJobRepository<UploadJob, UploadJobRepository>(inner, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(upload.Id, "descriptor-flush")).ShouldNotBeNull();
        using var fence = JobExecutionFenceScope.Enter(upload.Id, claim.FenceToken, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var work = new InterruptDescriptorBindingFlush(new EfUnitOfWork(db), db, cancel, cancellation);
        var requests = MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System);
        var intake = new OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            Substitute.For<IFileStorageService>(), Substitute.For<IJobItemRepository>(), work, requests, TimeProvider.System);

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => intake.ReconcileAsync(upload.Id, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            await Should.ThrowAsync<IOException>(() => intake.ReconcileAsync(upload.Id, cancellation.Token));

        work.SawPersistedBinding.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = database.CreateContext();
        (await verification.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
        (await verification.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        (await verification.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(0);
        (await verification.OriginalDiscLayouts.SingleAsync()).DescriptorFileId.ShouldBe(2);
        (await verification.JobItems.CountAsync()).ShouldBe(1);
        (await verification.RomFiles.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_StaleWorkerFence_RejectsBeforeBindingAndKeepsPriorProgress()
    {
        int layoutId = Decode((await Intake()).Id);
        await using var db = database.CreateContext();
        var upload = await SeedSchedulingUploadAsync(db, [layoutId]);
        // The setup has committed; the execution claim owns a fresh tracking scope.
        db.ChangeTracker.Clear();
        var repository = new UploadJobRepository(db, TimeProvider.System);
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(repository, db, TimeProvider.System, new());
        var first = (await claims.TryClaimExecutionAsync(upload.Id, "original")).ShouldNotBeNull();
        await using (var competing = database.CreateContext())
            await competing.Jobs.Where(row => row.Id == upload.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ExecutionLeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var second = (await claims.TryClaimExecutionAsync(upload.Id, "replacement")).ShouldNotBeNull();
        second.FenceToken.ShouldNotBe(first.FenceToken);
        using var stale = JobExecutionFenceScope.Enter(upload.Id, first.FenceToken, TimeProvider.System);
        var intake = new OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            Substitute.For<IFileStorageService>(), Substitute.For<IJobItemRepository>(), new EfUnitOfWork(db),
            MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), TimeProvider.System);

        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => intake.ReconcileAsync(upload.Id, default));

        (await db.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
        (await db.JobItems.CountAsync()).ShouldBe(1);
        (await db.OriginalDiscLayouts.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_LaterMatchedAttachmentCancelled_PreservesFirstAttachmentAndBothDurableOutcomes()
    {
        await using var db = database.CreateContext();
        byte[] cue = Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n");
        db.StageOriginalRom(new OriginalRomFixture
        {
            Id = 2,
            FileId = 2,
            OriginalFilename = "original.cue",
            Sha1 = Romd.Domain.Hashing.Sha1.FromSpan(System.Security.Cryptography.SHA1.HashData(cue)),
            Md5 = Romd.Domain.Hashing.Md5.FromSpan(System.Security.Cryptography.MD5.HashData(cue)),
            Crc32 = Romd.Domain.Hashing.Crc32.FromUInt32(1)
        });
        await db.PublishOriginalRomsAsync();
        var claim = await ClaimDescriptorUploadAsync(db);
        db.JobItems.AddRange(new[] { "a.cue", "b.cue" }.Select(path => JobItemEntity.FromDomain(JobItem.ForRom(
            claim.Job.Id, path, cue.Length, RomIngestOutcome.Ingested, 2, [], null, null, DateTimeOffset.UtcNow).WithUploadIdentity())));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var actual = new OriginalDiscLayoutService(db, new FileMutationLock(db));
        var descriptors = Substitute.For<IOriginalDiscDescriptorRepository>();
        descriptors.ReadUnattachedMatchedCuesAsync(claim.Job.Id, Arg.Any<CancellationToken>())
            .Returns(call => actual.ReadUnattachedMatchedCuesAsync(claim.Job.Id, call.Arg<CancellationToken>()));
        descriptors.RecordMatchedStagedAsync(claim.Job.Id, Arg.Any<OriginalDiscDescriptorSource>(), Arg.Any<OriginalCueDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var source = call.Arg<OriginalDiscDescriptorSource>();
                await actual.RecordMatchedStagedAsync(claim.Job.Id, source, call.Arg<OriginalCueDescriptor>(), call.Arg<CancellationToken>());
                if (source.RelativePath == "b.cue")
                {
                    await cancellation.CancelAsync();
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                }
            });
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(2, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(cue));
        var requests = Substitute.For<IMediaReconciliationRequests>();
        var intake = new OriginalDiscDescriptorIntake(descriptors, storage, Substitute.For<IJobItemRepository>(), new EfUnitOfWork(db), requests, TimeProvider.System);

        (await Should.ThrowAsync<OperationCanceledException>(() => intake.ReconcileAsync(claim.Job.Id, cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);

        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = database.CreateContext();
        (await verification.OriginalDiscLayouts.SingleAsync()).SourceRelativePath.ShouldBe("a.cue");
        (await verification.Set<OriginalDiscLayoutAuditEntity>().CountAsync()).ShouldBe(1);
        (await verification.JobItems.CountAsync()).ShouldBe(2);
        (await verification.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
        await requests.DidNotReceive().StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>());
    }

    private sealed class InterruptDescriptorBindingFlush(IUnitOfWork inner, RomdDbContext db, bool cancel,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public bool SawPersistedBinding { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => inner.BeginTransactionAsync(cancellationToken);
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            bool pending = db.ChangeTracker.Entries<OriginalDiscLayoutFileEntity>().Any(entry => entry.State == EntityState.Added);
            await inner.FlushAsync(cancellationToken);
            if (!pending)
                return;
            SawPersistedBinding = await db.OriginalDiscLayoutFiles.AnyAsync(cancellationToken);
            if (cancel)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected interruption after owner binding flush.");
        }
    }
}
