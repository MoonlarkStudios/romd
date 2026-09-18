using ErrorOr;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Artwork;
using Romd.Storage;
using Romd.Domain.Storage;
using System.Security.Cryptography;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkCurationPersistenceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task RequestAsync_PersistsCompositionAndRejectsChangedRetryOrStaleSelection()
    {
        await SeedAsync();
        var request = Request() with { FocalX = 0, FocalY = 100, ExpectedRevision = 0 };
        var result = await AcceptAsyncCore(request);
        result.IsError.ShouldBeFalse();
        await using var db = _database.CreateContext();
        var restored = (await db.Set<ArtworkImportJobEntity>().SingleAsync()).ToDomain();
        restored.FocalX.ShouldBe(0);
        restored.FocalY.ShouldBe(100);
        (await AcceptAsyncCore(request)).IsError.ShouldBeFalse();
        (await AcceptAsyncCore(request with { FocalX = 50 })).FirstError.Code.ShouldBe("Artwork.RequestConflict");
        (await AcceptAsyncCore(request with { RequestId = Guid.NewGuid() })).FirstError.Code.ShouldBe("Artwork.SelectionChanged");
        await PublishAsync(restored, Content(1));
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.FocalX.ShouldBe(0);
        selection.FocalY.ShouldBe(100);
    }

    [Theory]
    [InlineData(ArtworkRole.Hero)]
    [InlineData(ArtworkRole.Backdrop)]
    public async Task AutomaticPublication_RechecksMissingStateAndLeavesSelectionAutomatic(ArtworkRole role)
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var store = new Romd.Persistence.Artwork.ArtworkAcquisitionStore(db, Repository(db), TimeProvider.System);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await store.LockMissingAsync(1, role, 0, CancellationToken.None)).ShouldBeTrue();
            await Repository(db).StageLocalAssetAsync(1, role, "igdb", Content(9), Guid.Empty, CancellationToken.None, "42", "image");
            await store.RecordAsync(1, role, "Updated", "igdb", CancellationToken.None);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.Mode.ShouldBe(ArtworkSelectionMode.Automatic);
        selection.PinnedAssetId.ShouldBeNull();
        (await store.GetOutcomesAsync(1, CancellationToken.None)).Outcomes.Single().Status.ShouldBe("Updated");
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await store.LockMissingAsync(1, role, 0, CancellationToken.None)).ShouldBeFalse();
            await transaction.CommitAsync();
        }
        (await db.ArtworkAssets.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AutomaticPublication_ManualRequestAfterSnapshot_RejectsStaleRevision()
    {
        await SeedAsync();
        await AcceptAsyncCore(Request());
        await using var db = _database.CreateContext();
        var store = new Romd.Persistence.Artwork.ArtworkAcquisitionStore(db, Repository(db), TimeProvider.System);
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
        (await store.LockMissingAsync(1, ArtworkRole.Poster, 0, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task ArtworkSettings_RevisionConflict_DoesNotOverwriteNewSettings()
    {
        await using var db = _database.CreateContext();
        var store = new Romd.Persistence.Artwork.ArtworkAcquisitionStore(db, Repository(db), TimeProvider.System);
        var initial = await store.GetSettingsAsync(CancellationToken.None);
        initial.FillHeroes.ShouldBeTrue();
        initial.FillPosters.ShouldBeTrue();
        initial.FillLogos.ShouldBeTrue();
        initial.FillBackdrops.ShouldBeTrue();
        initial.ReviewBackdrops.ShouldBeTrue();
        (await store.UpdateSettingsAsync(new(initial.Revision, false, true, false), CancellationToken.None)).ShouldBeTrue();
        (await store.UpdateSettingsAsync(new(initial.Revision, true, false, true), CancellationToken.None)).ShouldBeFalse();
        var updated = await store.GetSettingsAsync(CancellationToken.None);
        updated.FillPosters.ShouldBeFalse();
        updated.FillLogos.ShouldBeFalse();
    }

    [Fact]
    public async Task PinMediaAsync_RetainsValidatedSnapshotAndPreservesOriginalType()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var bytes = new byte[] { 1, 2, 3 };
        var hash = Sha256.FromSpan(SHA256.HashData(bytes));
        var original = await new FileRepository(db).AddAsync(FileEntity.CreateNew(hash, 3, 3, false), CancellationToken.None);
        db.TitleMedia.Add(new TitleMediaEntity { Id = 1, TitleId = 1, FileId = original.Id, Type = "Cover", SourceId = "user", ContentType = "image/png", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var files = Substitute.For<IFileStorageService>();
        files.RetrieveByIdAsync(original.Id, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bytes)));
        var processor = Substitute.For<IArtworkImageProcessor>();
        processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(new ProcessedArtworkImage("image/png", 600, 900, [new ProcessedArtworkVariant("hero", "image/webp", 400, 600, [4, 5, 6])]));
        var cas = Substitute.For<IContentAddressableStore>();
        cas.StoreAsync(Arg.Any<Stream>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            using var buffer = new MemoryStream();
            await call.ArgAt<Stream>(0).CopyToAsync(buffer);
            return new StoreResult(StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(buffer.ToArray()))), buffer.Length, buffer.Length, false, false);
        });
        var service = new LocalArtworkService(Repository(db), processor, files, cas, new FileMutationLock(db), new EfUnitOfWork(db));

        (await service.PinMediaAsync(2, ArtworkRole.Hero, 1, 0, Guid.Empty)).IsError.ShouldBeTrue();
        (await service.PinMediaAsync(1, ArtworkRole.Hero, 1, 0, Guid.Empty, focalX: 0, focalY: 100)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.FocalX.ShouldBe(0);
        selection.FocalY.ShouldBe(100);
        (await db.TitleMedia.SingleAsync()).Type.ShouldBe("Cover");
        var asset = (await Repository(db).GetGalleryAsync(1, CancellationToken.None)).Value.Single();
        asset.Role.ShouldBe(ArtworkRole.Hero);
        asset.Original.FileId.ShouldBe(original.Id);
        asset.Variants.Count.ShouldBe(1);
        (await service.PinMediaAsync(1, ArtworkRole.Hero, 1, 0, Guid.Empty)).FirstError.Type.ShouldBe(ErrorType.Conflict);
        await db.TitleMedia.ExecuteDeleteAsync();
        var resolved = (await new ArtworkReader(db).ResolveAsync([1]))[1].Single(item => item.Role == ArtworkRole.Hero);
        resolved.Asset!.Id.ShouldBe(asset.Id);
        resolved.FocalX.ShouldBe(0);
        resolved.FocalY.ShouldBe(100);
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task PinAsync_RepinsRetainedArtworkWithoutImportAndRejectsWrongTitleRoleOrRevision()
    {
        await SeedAsync();
        var first = (await AcceptAsyncCore(Request())).Value;
        var firstAsset = await PublishAsync(first, Content(1));
        var second = (await AcceptAsyncCore(Request() with { ProviderAssetId = "second" })).Value;
        await PublishAsync(second, Content(3));
        await using (var db = _database.CreateContext())
        {
            (await Service(db).PinAsync(1, ArtworkRole.Hero, firstAsset, 0)).IsError.ShouldBeTrue();
            (await Service(db).PinAsync(2, ArtworkRole.Poster, firstAsset, 0)).IsError.ShouldBeTrue();
            (await Service(db).PinAsync(1, ArtworkRole.Poster, firstAsset, 0)).FirstError.Type.ShouldBe(ErrorType.Conflict);
            (await Service(db).PinAsync(1, ArtworkRole.Poster, firstAsset, second.SelectionRevision)).IsError.ShouldBeFalse();
        }
        await using var verify = _database.CreateContext();
        (await Repository(verify).GetGalleryAsync(1, CancellationToken.None)).Value.Count.ShouldBe(2);
        (await verify.ArtworkSelections.SingleAsync()).PinnedAssetId.ShouldBe(firstAsset);
        (await verify.Jobs.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task RequestAsync_ConcurrentIdenticalRequest_CommitsOneJobSelectionAndDispatch()
    {
        await SeedAsync();
        var request = Request();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<ErrorOr<ArtworkImportJob>> AcceptAsync()
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task;
            return await AcceptAsyncCore(request);
        }

        var results = await Task.WhenAll(AcceptAsync(), AcceptAsync());

        results.ShouldAllBe(result => !result.IsError);
        results.Select(result => result.Value.Id).Distinct().ShouldBe([request.RequestId]);
        await using var db = _database.CreateContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.JobDispatches.SingleAsync()).JobId.ShouldBe(request.RequestId);
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.Revision.ShouldBe(1);
        selection.PendingRequestId.ShouldBe(request.RequestId);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Automatic);
    }

    [Fact]
    public async Task RequestAsync_RequestIdentityReusedForAnotherTitle_ReturnsConflict()
    {
        await SeedAsync();
        var request = Request();
        (await AcceptAsyncCore(request)).IsError.ShouldBeFalse();

        var result = await AcceptAsyncCore(request with { TitleId = 2 });

        result.FirstError.Code.ShouldBe("Artwork.RequestConflict");
        await using var db = _database.CreateContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.ArtworkSelections.SingleAsync()).TitleId.ShouldBe(1);
    }

    [Fact]
    public async Task RequestAsync_ReplayAfterPublication_DoesNotCreateNewIntentOrAsset()
    {
        await SeedAsync();
        var request = Request();
        var first = (await AcceptAsyncCore(request)).Value;
        var assetId = await PublishAsync(first, Content(1));

        var replay = await AcceptAsyncCore(request);

        replay.IsError.ShouldBeFalse();
        replay.Value.Id.ShouldBe(first.Id);
        replay.Value.RetainedAssetId.ShouldBe(assetId);
        await using var db = _database.CreateContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        (await db.ArtworkAssets.CountAsync()).ShouldBe(1);
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.Revision.ShouldBe(first.SelectionRevision);
        selection.PendingRequestId.ShouldBeNull();
        selection.PinnedAssetId.ShouldBe(assetId);
    }

    [Fact]
    public async Task ReturnToAutomaticAsync_PendingImport_FencesOldPublication()
    {
        await SeedAsync();
        var job = (await AcceptAsyncCore(Request())).Value;
        await using (var db = _database.CreateContext())
        {
            var result = await Service(db).ReturnToAutomaticAsync(1, ArtworkRole.Poster);
            result.Value.ShouldBeGreaterThan(job.SelectionRevision);
        }

        await using var verify = _database.CreateContext();
        await using var transaction = await new EfUnitOfWork(verify).BeginTransactionAsync();
        (await Repository(verify).LockPendingAsync(job, CancellationToken.None)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Repository(verify).StageAssetAsync(job, Content(1), CancellationToken.None));
        (await verify.ArtworkAssets.CountAsync()).ShouldBe(0);
        var selection = await verify.ArtworkSelections.SingleAsync();
        selection.Mode.ShouldBe(ArtworkSelectionMode.Automatic);
        selection.PendingRequestId.ShouldBeNull();
    }

    [Fact]
    public async Task Publication_RollbackAfterOutcomeWrite_PreservesOldPinAndResumableJob()
    {
        await SeedAsync();
        var first = (await AcceptAsyncCore(Request())).Value;
        var previousId = await PublishAsync(first, Content(1));
        var replacement = (await AcceptAsyncCore(Request() with { ProviderAssetId = "replacement" })).Value;
        var claim = await ClaimAsync(replacement.Id);
        await using (var db = _database.CreateContext())
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            var unit = new EfUnitOfWork(db);
            await using var transaction = await unit.BeginTransactionAsync();
            var repository = Repository(db);
            await repository.StageAssetAsync(claim.Job, Content(3), CancellationToken.None);
            await unit.FlushAsync();
            var assetId = await repository.StagePublicationAsync(claim.Job, Hash(3).ToString(), CancellationToken.None);
            await repository.StageOutcomeAsync(claim.Job, assetId, false, CancellationToken.None);
            await unit.FlushAsync();
            await using var observer = _database.CreateContext();
            (await observer.ArtworkSelections.SingleAsync()).PinnedAssetId.ShouldBe(previousId);
            await transaction.RollbackAsync();
        }

        await using var verify = _database.CreateContext();
        var selection = await verify.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(previousId);
        selection.PendingRequestId.ShouldBe(replacement.Id);
        (await verify.ArtworkAssets.CountAsync()).ShouldBe(1);
        (await verify.Files.CountAsync()).ShouldBe(2);
        var savedJob = await verify.Jobs.OfType<ArtworkImportJobEntity>().SingleAsync(job => job.Id == replacement.Id);
        savedJob.Phase.ShouldBe("Importing");
        savedJob.RetainedAssetId.ShouldBeNull();
        savedJob.WasSuperseded.ShouldBeFalse();
        claim.Job.RetainedAssetId.ShouldBeNull();
    }

    [Fact]
    public async Task Publication_ConcurrentTitlesWithIdenticalContent_DeduplicatesFileMetadata()
    {
        await SeedAsync();
        var first = (await AcceptAsyncCore(Request())).Value;
        var second = (await AcceptAsyncCore(Request() with { TitleId = 2 })).Value;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<int> PublishTogether(ArtworkImportJob job)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task;
            return await PublishAsync(job, Content(1));
        }

        var ids = await Task.WhenAll(PublishTogether(first), PublishTogether(second));

        ids.Distinct().Count().ShouldBe(2);
        await using var db = _database.CreateContext();
        (await db.Files.CountAsync()).ShouldBe(2);
        (await db.ArtworkAssets.CountAsync()).ShouldBe(2);
        (await db.ArtworkVariants.Select(variant => variant.FileId).Distinct().CountAsync()).ShouldBe(1);
        (await db.ArtworkAssets.Select(asset => asset.OriginalFileId).Distinct().CountAsync()).ShouldBe(1);
        (await db.ArtworkSelections.CountAsync(selection => selection.Mode == ArtworkSelectionMode.Pinned)).ShouldBe(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TerminalCheckpoint_ClearsOnlyMatchingPendingAndPreservesPin(bool cancel, bool newerRequest)
    {
        await SeedAsync();
        var previous = (await AcceptAsyncCore(Request())).Value;
        var previousAsset = await PublishAsync(previous, Content(1));
        var pending = (await AcceptAsyncCore(Request() with { ProviderAssetId = "replacement" })).Value;
        var claim = await ClaimAsync(pending.Id);
        ArtworkImportJob? newer = newerRequest
            ? (await AcceptAsyncCore(Request() with { ProviderAssetId = "newest" })).Value : null;
        if (cancel) claim.Job.Cancel();
        else claim.Job.Fail("Invalid image.");

        await using (var db = _database.CreateContext())
        {
            var claims = Claims(db);
            (await claims.TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeTrue();
        }

        await using var verify = _database.CreateContext();
        var selection = await verify.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(previousAsset);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
        selection.PendingRequestId.ShouldBe(newer?.Id);
        (await verify.Jobs.SingleAsync(job => job.Id == pending.Id)).Phase.ShouldBe(cancel ? "Cancelled" : "Failed");
    }

    [Fact]
    public async Task TerminalCheckpoint_WithoutTransaction_RejectsBeforeChangingJob()
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        pending.Cancel();
        await using var db = _database.CreateContext();

        await Should.ThrowAsync<InvalidOperationException>(() => new ArtworkImportJobRepository(db, TimeProvider.System)
            .UpdateAsync(pending));

        (await db.Jobs.SingleAsync()).Phase.ShouldBe("Pending");
        (await db.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(pending.Id);
    }

    [Fact]
    public async Task TerminalCheckpoint_RolledBack_RevertsJobAndPendingCleanupTogether()
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        var claim = await ClaimAsync(pending.Id);
        claim.Job.Cancel();
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateAsync(claim.Job);
            await transaction.RollbackAsync();
        }

        await using var verify = _database.CreateContext();
        (await verify.Jobs.SingleAsync()).Phase.ShouldBe("Importing");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(pending.Id);
    }

    [Fact]
    public async Task TerminalCheckpoint_StaleCancellationAfterPublication_PreservesCommittedAssetOutcome()
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        var retainedId = await PublishAsync(pending, Content(1), complete: false);
        pending.Cancel();
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            await new JobRepository(db, TimeProvider.System).UpdateAsync(pending);
            await transaction.CommitAsync();
        }

        await using var verify = _database.CreateContext();
        var job = await verify.Jobs.OfType<ArtworkImportJobEntity>().SingleAsync();
        job.Phase.ShouldBe("Cancelled");
        job.RetainedAssetId.ShouldBe(retainedId);
        job.WasSuperseded.ShouldBeFalse();
        var selection = await verify.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(retainedId);
        selection.PendingRequestId.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryCheckpoint_StaleAfterCommittedOutcome_PreservesOutcome(bool superseded)
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        var claim = await ClaimAsync(pending.Id);
        int? retainedId = null;
        await using (var db = _database.CreateContext())
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            var unit = new EfUnitOfWork(db);
            await using var transaction = await unit.BeginTransactionAsync();
            var repository = Repository(db);
            if (!superseded)
            {
                var content = Content(1);
                await repository.StageAssetAsync(claim.Job, content, CancellationToken.None);
                await unit.FlushAsync();
                retainedId = await repository.StagePublicationAsync(claim.Job, content.Original.Hash.ToString(), CancellationToken.None);
            }
            await repository.StageOutcomeAsync(claim.Job, retainedId, superseded, CancellationToken.None);
            await transaction.CommitAsync();
        }

        // Simulate a lost commit acknowledgement: the worker still has no outcome.
        claim.Job.RetainedAssetId.ShouldBeNull();
        claim.Job.WasSuperseded.ShouldBeFalse();
        claim.Job.RecordError("attempt", "Commit acknowledgement was interrupted.");
        await using (var checkpoint = _database.CreateContext())
            (await Claims(checkpoint).TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeTrue();

        await using var verify = _database.CreateContext();
        var persisted = await verify.Jobs.OfType<ArtworkImportJobEntity>().SingleAsync();
        persisted.Phase.ShouldBe("Importing");
        persisted.RetainedAssetId.ShouldBe(retainedId);
        persisted.WasSuperseded.ShouldBe(superseded);
        if (!superseded)
            (await verify.ArtworkSelections.SingleAsync()).PinnedAssetId.ShouldBe(retainedId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStateFilter_ClearsOnlyMatchingPendingIntent(bool newerRequest)
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        pending.Start("delivery-1");
        await using (var db = _database.CreateContext())
            await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateAsync(pending);
        ArtworkImportJob? newer = newerRequest ? (await AcceptAsyncCore(Request())).Value : null;
        var services = new ServiceCollection();
        services.AddDbContext<RomdDbContext>(options => options.UseNpgsql(_database.ConnectionString));
        using var provider = services.BuildServiceProvider();
        var filter = new HangfireJobStateSyncFilter(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<HangfireJobStateSyncFilter>.Instance);
        var invocation = Hangfire.Common.Job.FromExpression<ArtworkImportJobHangfireHandler>(
            handler => handler.ExecuteAsync(pending.Id, null!));
        var backgroundJob = new BackgroundJob("delivery-1", invocation, DateTime.UtcNow);
        var context = new ApplyStateContext(Substitute.For<JobStorage>(), Substitute.For<IStorageConnection>(),
            Substitute.For<IWriteOnlyTransaction>(), backgroundJob,
            new FailedState(new InvalidOperationException("Retries exhausted.")), ProcessingState.StateName);

        filter.OnStateApplied(context, Substitute.For<IWriteOnlyTransaction>());

        await using var verify = _database.CreateContext();
        (await verify.Jobs.SingleAsync(job => job.Id == pending.Id)).Phase.ShouldBe("Failed");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(newer?.Id);
        (await verify.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task TitleDeletion_WhileImportOwnsJobRow_PreservesHistoryAndSupersedesImport()
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        var claim = await ClaimAsync(pending.Id);
        await using (var worker = _database.CreateContext())
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            await using var transaction = await new EfUnitOfWork(worker).BeginTransactionAsync();
            await worker.Jobs.Where(job => job.Id == pending.Id).ExecuteUpdateAsync(setters =>
                setters.SetProperty(job => job.CurrentItem, job => job.CurrentItem));
            // Deletion must not cascade into the worker-owned job row or wait for it.
            await using (var deletion = _database.CreateContext())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                (await deletion.Titles.Where(title => title.Id == pending.TitleId)
                    .ExecuteDeleteAsync(timeout.Token)).ShouldBe(1);
            }
            var repository = Repository(worker);
            (await repository.LockPendingAsync(claim.Job, CancellationToken.None)).ShouldBeFalse();
            await repository.StageOutcomeAsync(claim.Job, null, true, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var verify = _database.CreateContext();
        var history = await verify.Jobs.OfType<ArtworkImportJobEntity>().SingleAsync();
        history.TitleId.ShouldBe(pending.TitleId);
        history.WasSuperseded.ShouldBeTrue();
        (await verify.ArtworkSelections.CountAsync()).ShouldBe(0);
    }

    public void Dispose() => _database.Dispose();

    private async Task<ErrorOr<ArtworkImportJob>> AcceptAsyncCore(ArtworkImportRequest request)
    {
        await using var db = _database.CreateContext();
        return await Service(db).RequestAsync(request);
    }

    private async Task<JobExecutionClaim<ArtworkImportJob>> ClaimAsync(Guid id)
    {
        await using var db = _database.CreateContext();
        return (await Claims(db).TryClaimExecutionAsync(id, $"delivery-{id}"))!;
    }

    private async Task<int> PublishAsync(ArtworkImportJob pending, RetainedArtworkContent content, bool complete = true)
    {
        var claim = await ClaimAsync(pending.Id);
        int assetId;
        await using (var db = _database.CreateContext())
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            var unit = new EfUnitOfWork(db);
            await using var transaction = await unit.BeginTransactionAsync();
            var repository = Repository(db);
            await repository.StageAssetAsync(claim.Job, content, CancellationToken.None);
            await unit.FlushAsync();
            assetId = await repository.StagePublicationAsync(claim.Job, content.Original.Hash.ToString(), CancellationToken.None);
            await repository.StageOutcomeAsync(claim.Job, assetId, false, CancellationToken.None);
            await transaction.CommitAsync();
        }
        if (!complete) return assetId;
        claim.Job.SetImportOutcome(assetId, false);
        claim.Job.Complete();
        await using var checkpoint = _database.CreateContext();
        (await Claims(checkpoint).TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeTrue();
        return assetId;
    }

    private static ArtworkCurationRepository Repository(RomdDbContext db) => new(db, TimeProvider.System);
    private static ArtworkCurationService Service(RomdDbContext db) => new(Repository(db), new EfUnitOfWork(db));
    private static ClaimedJobRepository<ArtworkImportJob, ArtworkImportJobRepository> Claims(RomdDbContext db) =>
        new(new ArtworkImportJobRepository(db, TimeProvider.System), db, TimeProvider.System, new JobExecutionClaimOptions());
    private static ArtworkImportRequest Request() => new(Guid.NewGuid(), 1, ArtworkRole.Poster, "steamgriddb",
        "game", "asset", "https://cdn2.steamgriddb.com/grid/asset.png", "Artist", null);
    private static RetainedArtworkContent Content(byte hash) => new(
        new RetainedArtworkFile(Hash(hash), 100, 100, false, "image/png", 600, 900, "original"),
        [new RetainedArtworkFile(Hash((byte)(hash + 1)), 50, 50, false, "image/webp", 300, 450, "card")],
        "Artist", "https://www.steamgriddb.com/grid/asset");
    private static Sha256 Hash(byte first)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = first;
        return Sha256.FromBytes(bytes);
    }

    private async Task SeedAsync()
    {
        await using var db = _database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1, Name = "Test platform", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test platform", BaseCompactLabel = "Test platform", CanonicalKey = "test", ShortName = "test", Manufacturer = "Test",
            CreatedAt = now, CreatedByUserId = Guid.Empty
        });
        for (var id = 1; id <= 2; id++)
            db.Titles.Add(new TitleEntity
            {
                Id = id, PlatformId = 1, Name = $"Title {id}", NormalizedName = $"title {id}",
                EnrichmentStatus = "None", FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}",
                ScreenshotPrefsJson = "{}", CreatedAt = now, CreatedByUserId = Guid.Empty
            });
        await db.SaveChangesAsync();
    }
}
