using Romd.Infrastructure.Tests.Helpers;
using ErrorOr;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Commands.PinTitleMediaArtwork;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Romd.Domain.Storage;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed partial class ArtworkCurationPersistenceTests : IDisposable
{
    private const int SeededFiles = 12;
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly Dictionary<Sha256, int> _files = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"romd-artwork-curation-{Guid.NewGuid():N}");

    [Fact]
    public async Task Acquisition_ResumedRunPreservesUpdatedOutcome_NewRunRecordsAlreadyPresent()
    {
        await SeedAsync();
        var runId = Guid.NewGuid();
        async Task Record(Guid run, string status, string? source)
        {
            await using var db = _database.CreateContext();
            var store = new Romd.Persistence.Artwork.ArtworkAcquisitionStore(db, Repository(db), TimeProvider.System);
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            await store.RecordAsync(1, ArtworkRole.Poster, status, source, default, run);
            await transaction.CommitAsync();
        }
        await Record(runId, "Updated", "steamgriddb");
        await Record(runId, "AlreadyPresent", null);
        await using (var db = _database.CreateContext())
        {
            var outcome = await db.Set<ArtworkAcquisitionEntity>().SingleAsync();
            outcome.Status.ShouldBe("Updated");
            outcome.SourceId.ShouldBe("steamgriddb");
            outcome.RunId.ShouldBe(runId);
        }
        var nextRunId = Guid.NewGuid();
        await Record(nextRunId, "AlreadyPresent", null);
        await using (var db = _database.CreateContext())
        {
            var outcome = await db.Set<ArtworkAcquisitionEntity>().SingleAsync();
            outcome.Status.ShouldBe("AlreadyPresent");
            outcome.RunId.ShouldBe(nextRunId);
        }
    }

    [Fact]
    public async Task RequestAsync_PersistsCompositionAndRejectsChangedRetryOrStaleSelection()
    {
        await SeedAsync();
        var request = Request() with { FocalX = 0, FocalY = 100, IfMatch = Role(0) };
        var result = await AcceptAsyncCore(request);
        result.IsError.ShouldBeFalse();
        await using var db = _database.CreateContext();
        var restored = (await db.Set<ArtworkImportJobEntity>().SingleAsync()).ToDomain();
        restored.FocalX.ShouldBe(0);
        restored.FocalY.ShouldBe(100);
        (await AcceptAsyncCore(request)).IsError.ShouldBeFalse();
        (await AcceptAsyncCore(request with { FocalX = 50 })).FirstError.Code.ShouldBe("Artwork.RequestConflict");
        (await AcceptAsyncCore(request with { RequestId = Guid.NewGuid() })).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
        await PublishAsync(restored, Content(1));
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.FocalX.ShouldBe(0);
        selection.FocalY.ShouldBe(100);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSharedTitleCopiesExcludesProviderAndFencesPendingImport()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var repository = Repository(db);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await repository.StageLocalAssetAsync(1, ArtworkRole.Poster, "steamgriddb", Content(9), default, "42", "image");
            await repository.StageLocalAssetAsync(1, ArtworkRole.Hero, "user", Content(9), default);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var assets = await db.ArtworkAssets.ToListAsync();
        db.TitleMedia.Add(new TitleMediaEntity
        {
            Id = 10,
            TitleId = 1,
            FileId = assets[0].OriginalFileId,
            Type = "Cover",
            SourceId = "igdb",
            IsPrimary = true,
            SourceUrl = "https://example.test/image.png",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = Service(db);
        foreach (var asset in assets)
            (await service.PinAsync(1, asset.Role, asset.Id, Role(0))).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var stale = await FamilyAsync(repository);
        var pending = (await service.RequestAsync(Request() with { ProviderAssetId = "another", IfMatch = Role(1) })).Value;
        db.ChangeTracker.Clear();
        // The import rotated the Poster revision, so the family validator read before it is stale.
        (await service.DeleteAsync(1, assets[0].Id, stale, default)).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
        db.ChangeTracker.Clear();
        (await db.TitleMedia.CountAsync()).ShouldBe(1);
        var deleted = await service.DeleteAsync(1, assets[0].Id, await FamilyAsync(repository), default);
        deleted.IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        // The answer is the family as the delete wrote it: the validator a read then returns.
        deleted.Value.Revision.ShouldBe((await repository.GetFamilyAsync(1, default)).Value.Revision);
        db.ChangeTracker.Clear();
        (await db.TitleMedia.CountAsync()).ShouldBe(0);
        (await repository.GetGalleryAsync(1, default)).Value.ShouldBeEmpty();
        (await db.ArtworkAssets.CountAsync(x => x.IsRejected)).ShouldBe(2);
        (await repository.GetExcludedUrlsAsync(1, default)).ShouldContain("https://example.test/image.png");
        var store = new Romd.Persistence.Artwork.ArtworkAcquisitionStore(db, repository, TimeProvider.System);
        (await store.GetRejectedCandidatesAsync(1, ArtworkRole.Poster, "steamgriddb", "42", default)).ShouldContain("image");
        (await new ArtworkReader(db).ResolveAsync([1]))[1].ShouldAllBe(x => x.Asset == null && x.LegacyCover == null);
        await using var fence = await new EfUnitOfWork(db).BeginTransactionAsync();
        (await repository.LockPendingAsync(pending, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task HideAsync_PersistsWithoutAssetsAndPreventsStaleImport()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var request = Request();
        var revision = (await db.Titles.SingleAsync(x => x.Id == 1)).Revision;
        (await Service(db).HideAsync(1, request.TrustedAssetUrl, Family(Guid.NewGuid().ToString()), default)).FirstError.Code
            .ShouldBe("Artwork.PreconditionFailed");
        (await Service(db).HideAsync(1, request.TrustedAssetUrl, await FamilyAsync(Repository(db)), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await db.ArtworkAssets.CountAsync()).ShouldBe(0);
        (await db.Titles.SingleAsync(x => x.Id == 1)).Revision.ShouldNotBe(revision);
        (await Repository(db).GetExcludedUrlsAsync(1, default)).ShouldContain(request.TrustedAssetUrl);
        (await Service(db).RequestAsync(request with { IfMatch = Role(1) })).FirstError.Code.ShouldBe("Artwork.InvalidRequest");
    }

    [Fact]
    public async Task HideAsync_EveryRolePendingAnUnrelatedImport_StillChangesTheFamilyValidator()
    {
        await SeedAsync();
        foreach (var role in Enum.GetValues<ArtworkRole>())
            (await AcceptAsyncCore(Request() with { Role = role, ProviderAssetId = $"pending-{role}" })).IsError.ShouldBeFalse();
        await using var db = _database.CreateContext();
        var repository = Repository(db);
        var before = (await repository.GetFamilyAsync(1, default)).Value;
        before.Roles.ShouldAllBe(role => role.PendingJobId != null);

        var hidden = await Service(db).HideAsync(1, "https://cdn2.steamgriddb.com/grid/unrelated.png", Family(before.Revision), default);

        // No role revision moved: every pending import is for other artwork, so none is fenced.
        hidden.Value.Roles.Select(role => role.Revision).ShouldBe(before.Roles.Select(role => role.Revision));
        hidden.Value.Revision.ShouldNotBe(before.Revision);
        db.ChangeTracker.Clear();
        (await repository.GetFamilyAsync(1, default)).Value.Revision.ShouldBe(hidden.Value.Revision);
        (await Service(db).HideAsync(1, "https://cdn2.steamgriddb.com/grid/other.png", Family(before.Revision), default))
            .FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
    }

    [Fact]
    public async Task ReplaceRole_ConcurrentWritersWithOneETag_TheSecondWaitsForTheTitleLockThenIs412()
    {
        await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var first = _database.CreateContext();
        await using var transaction = await new EfUnitOfWork(first).BeginTransactionAsync(timeout.Token);
        (await Repository(first).StageEmptyAsync(1, ArtworkRole.Logo, Role(0), timeout.Token)).IsError.ShouldBeFalse();
        await first.SaveChangesAsync(timeout.Token);
        await using var second = _database.CreateContext();

        var (pending, waited) = await _database.StartWaitingForALockAsync(second,
            ct => Service(second).ReturnToAutomaticAsync(1, ArtworkRole.Logo, Role(0), ct), timeout.Token);
        await transaction.CommitAsync(timeout.Token);

        waited.ShouldBeTrue();
        (await pending.WaitAsync(timeout.Token)).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
        await using var read = _database.CreateContext();
        (await read.ArtworkSelections.SingleAsync(x => x.TitleId == 1 && x.Role == ArtworkRole.Logo, timeout.Token))
            .Mode.ShouldBe(ArtworkSelectionMode.Empty);
    }

    [Fact]
    public async Task DeleteAsset_ConcurrentWritersWithOneETag_TheSecondWaitsForTheTitleLockThenIs412()
    {
        await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var seed = _database.CreateContext())
        await using (var staging = await new EfUnitOfWork(seed).BeginTransactionAsync(timeout.Token))
        {
            await Repository(seed).StageLocalAssetAsync(1, ArtworkRole.Poster, "user", Content(1), timeout.Token);
            await Repository(seed).StageLocalAssetAsync(1, ArtworkRole.Hero, "user", Content(3), timeout.Token);
            await staging.CommitAsync(timeout.Token);
        }
        await using var first = _database.CreateContext();
        var assets = await first.ArtworkAssets.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(timeout.Token);
        var shown = await FamilyAsync(Repository(first));
        await using var transaction = await new EfUnitOfWork(first).BeginTransactionAsync(timeout.Token);
        (await Repository(first).StageDeleteAsync(1, assets[0], shown, timeout.Token)).IsError.ShouldBeFalse();
        await first.SaveChangesAsync(timeout.Token);
        await using var second = _database.CreateContext();

        var (pending, waited) = await _database.StartWaitingForALockAsync(second,
            ct => Service(second).DeleteAsync(1, assets[1], shown, ct), timeout.Token);
        await transaction.CommitAsync(timeout.Token);

        waited.ShouldBeTrue();
        (await pending.WaitAsync(timeout.Token)).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
        await using var read = _database.CreateContext();
        (await Repository(read).GetGalleryAsync(1, timeout.Token)).Value.Select(x => x.Id).ShouldBe([assets[1]]);
    }

    [Fact]
    public async Task TitleMediaRemoval_RejectsItsAssetsExcludesItsUrlAndReturnsItsRolesToAutomatic()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var repository = Repository(db);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await repository.StageLocalAssetAsync(1, ArtworkRole.Poster, "igdb", Content(9), default);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var asset = await db.ArtworkAssets.SingleAsync();
        (await Service(db).PinAsync(1, ArtworkRole.Poster, asset.Id, Role(0))).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var before = (await repository.GetFamilyAsync(1, default)).Value.Revision;
        const string url = "https://images.igdb.com/cover.png";

        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await repository.StageTitleMediaRemovalAsync(1, 10, asset.OriginalFileId, url, default);
            await transaction.CommitAsync();
        }

        db.ChangeTracker.Clear();
        (await db.ArtworkAssets.SingleAsync()).IsRejected.ShouldBeTrue();
        (await repository.GetExcludedUrlsAsync(1, default)).ShouldContain(url);
        var family = (await repository.GetFamilyAsync(1, default)).Value;
        family.Roles.Single(role => role.Role == ArtworkRole.Poster).Mode.ShouldBe(ArtworkSelectionMode.Automatic);
        family.Revision.ShouldNotBe(before);
        // The excluded URL is never imported again.
        (await Service(db).RequestAsync(Request() with { TrustedAssetUrl = url, IfMatch = Role(2) })).FirstError.Code
            .ShouldBe("Artwork.InvalidRequest");
    }

    [Fact]
    public async Task TitleMediaRemoval_AnotherRowStillShowsTheFile_ExcludesOnlyTheDeletedRowsUrl()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var repository = Repository(db);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await repository.StageLocalAssetAsync(1, ArtworkRole.Poster, "igdb", Content(9), default);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var asset = await db.ArtworkAssets.SingleAsync();
        const string deletedUrl = "https://images.igdb.com/cover.png";
        db.TitleMedia.AddRange(
            new TitleMediaEntity
            {
                Id = 10,
                TitleId = 1,
                FileId = asset.OriginalFileId,
                Type = "Cover",
                SourceId = "igdb",
                SourceUrl = deletedUrl,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new TitleMediaEntity
            {
                Id = 11,
                TitleId = 1,
                FileId = asset.OriginalFileId,
                Type = "Cover",
                SourceId = "user",
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        (await Service(db).PinAsync(1, ArtworkRole.Poster, asset.Id, Role(0))).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var poster = (await repository.GetFamilyAsync(1, default)).Value.Roles.Single(role => role.Role == ArtworkRole.Poster);

        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            db.TitleMedia.Remove(await db.TitleMedia.AsTracking().SingleAsync(media => media.Id == 10));
            await repository.StageTitleMediaRemovalAsync(1, 10, asset.OriginalFileId, deletedUrl, default);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        db.ChangeTracker.Clear();
        // The user's copy still shows the file, so its asset and the role showing it stay; only the deleted row's
        // source is excluded from acquisition.
        (await db.ArtworkAssets.SingleAsync()).IsRejected.ShouldBeFalse();
        (await repository.GetFamilyAsync(1, default)).Value.Roles.Single(role => role.Role == ArtworkRole.Poster)
            .ShouldBe(poster);
        (await repository.GetExcludedUrlsAsync(1, default)).ShouldBe([deletedUrl]);
    }

    [Fact]
    public async Task RejectedCandidate_StaysExcludedUntilRestoredAndEmptyRoleFencesPublication()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var repository = Repository(db);
        var store = new Romd.Persistence.Artwork.ArtworkAcquisitionStore(db, repository, TimeProvider.System);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await repository.StageLocalAssetAsync(1, ArtworkRole.Poster, "steamgriddb", Content(9), default, "42", "image");
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var asset = await db.ArtworkAssets.SingleAsync();
        var service = new ArtworkCurationService(repository, new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtworkCurationService>.Instance);
        var before = await FamilyAsync(repository);
        (await service.RejectAsync(1, asset.Id, true, before, default)).Value.Roles
            .Single(state => state.Role == ArtworkRole.Poster).Revision.ShouldBe(1);
        db.ChangeTracker.Clear();
        (await repository.GetGalleryAsync(1, default)).Value.ShouldBeEmpty();
        (await store.GetRejectedCandidatesAsync(1, ArtworkRole.Poster, "steamgriddb", "42", default)).ShouldBe(["image"]);
        (await store.GetRejectedCandidatesAsync(1, ArtworkRole.Poster, "steamgriddb", "43", default)).ShouldBeEmpty();
        (await service.RejectAsync(1, asset.Id, false, before, default)).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
        db.ChangeTracker.Clear();
        (await service.RejectAsync(1, asset.Id, false, await FamilyAsync(repository), default)).Value.Roles
            .Single(state => state.Role == ArtworkRole.Poster).Revision.ShouldBe(2);
        db.ChangeTracker.Clear();
        (await service.RejectAsync(1, int.MaxValue, true, await FamilyAsync(repository), default)).FirstError.Code
            .ShouldBe("Artwork.AssetNotFound");
        db.ChangeTracker.Clear();
        (await repository.GetGalleryAsync(1, default)).Value.Count.ShouldBe(1);
        (await service.LeaveEmptyAsync(1, ArtworkRole.Logo, Role(0), default)).Value.Revision.ShouldBe(1);
        db.ChangeTracker.Clear();
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await store.LockMissingAsync(1, ArtworkRole.Logo, 1, default)).ShouldBeFalse();
        }
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
            await Repository(db).StageLocalAssetAsync(1, role, "igdb", Content(9), CancellationToken.None, "42", "image");
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
        initial.ReviewBackdrops.ShouldBeFalse();
        var handler = new Romd.Admin.Application.Artwork.Commands.ReplaceArtworkEnrichmentSettings.ReplaceArtworkEnrichmentSettingsCommandHandler(
            store, [], new EfUnitOfWork(db), NullLogger<Romd.Admin.Application.Artwork.Commands.ReplaceArtworkEnrichmentSettings.ReplaceArtworkEnrichmentSettingsCommandHandler>.Instance);
        (await handler.HandleAsync(new(new(false, true, false), RevisionPrecondition.For(initial.Revision.ToString())))).IsError.ShouldBeFalse();
        (await handler.HandleAsync(new(new(true, false, true), RevisionPrecondition.For(initial.Revision.ToString()))))
            .FirstError.Code.ShouldBe("ArtworkEnrichment.PreconditionFailed");
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
        await using var storage = new ServiceCollection()
            .AddContentAddressableStorage(options => options.RootPath = _root).BuildServiceProvider();
        var cas = storage.GetRequiredService<IContentAddressableStore>();
        var stored = await cas.StoreAsync(new MemoryStream(bytes));
        var original = await new FileRepository(db).CreateAndCommitAsync(db,
            FileEntity.CreateNew(stored.Key.Hash, stored.Size, stored.StoredSize, stored.IsCompressed, now: DateTimeOffset.UnixEpoch), CancellationToken.None);
        db.TitleMedia.Add(new TitleMediaEntity { Id = 1, TitleId = 1, FileId = original.Id, Type = "Cover", SourceId = "user", ContentType = "image/png", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var files = new FileStorageService(cas, new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db),
            new AdminRealtimeOutbox(db, TimeProvider.System), NullLogger<FileStorageService>.Instance, clock: TimeProvider.System);
        var processor = Substitute.For<IArtworkImageProcessor>();
        processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(new ProcessedArtworkImage("image/png", 600, 900, [new ProcessedArtworkVariant("hero", "image/webp", 400, 600, [4, 5, 6])]));
        var handler = new PinTitleMediaArtworkCommandHandler(Repository(db), processor, files, new EfUnitOfWork(db),
            NullLogger<PinTitleMediaArtworkCommandHandler>.Instance);

        (await handler.HandleAsync(new(2, ArtworkRole.Hero, 1, Role(0)))).IsError.ShouldBeTrue();
        var pinned = await handler.HandleAsync(new(1, ArtworkRole.Hero, 1, Role(0), FocalX: 0, FocalY: 100));
        (pinned.Value.Role, pinned.Value.Mode, pinned.Value.Revision).ShouldBe((ArtworkRole.Hero, ArtworkSelectionMode.Pinned, 1));
        db.ChangeTracker.Clear();
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.FocalX.ShouldBe(0);
        selection.FocalY.ShouldBe(100);
        (await db.TitleMedia.SingleAsync()).Type.ShouldBe("Cover");
        var asset = (await Repository(db).GetGalleryAsync(1, CancellationToken.None)).Value.Single();
        asset.Role.ShouldBe(ArtworkRole.Hero);
        asset.Original.FileId.ShouldBe(original.Id);
        asset.Variants.Count.ShouldBe(1);
        (await handler.HandleAsync(new(1, ArtworkRole.Hero, 1, Role(0)))).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
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
            (await Service(db).PinAsync(1, ArtworkRole.Hero, firstAsset, Role(0))).IsError.ShouldBeTrue();
            (await Service(db).PinAsync(2, ArtworkRole.Poster, firstAsset, Role(0))).IsError.ShouldBeTrue();
            (await Service(db).PinAsync(1, ArtworkRole.Poster, firstAsset, Role(0))).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
            var repinned = await Service(db).PinAsync(1, ArtworkRole.Poster, firstAsset, Role(second.SelectionRevision));
            repinned.Value.Revision.ShouldBe(second.SelectionRevision + 1);
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
            if (Interlocked.Increment(ref arrivals) == 2)
                ready.SetResult();
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
            var result = await Service(db).ReturnToAutomaticAsync(1, ArtworkRole.Poster, Role(job.SelectionRevision));
            result.Value.Revision.ShouldBeGreaterThan(job.SelectionRevision);
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
        (await verify.Files.CountAsync()).ShouldBe(SeededFiles);
        var savedJob = await verify.Jobs.OfType<ArtworkImportJobEntity>().SingleAsync(job => job.Id == replacement.Id);
        savedJob.Phase.ShouldBe("Importing");
        savedJob.RetainedAssetId.ShouldBeNull();
        savedJob.WasSuperseded.ShouldBeFalse();
        claim.Job.RetainedAssetId.ShouldBeNull();
    }

    [Fact]
    public async Task Publication_ConcurrentTitlesWithIdenticalContent_ShareFileRows()
    {
        await SeedAsync();
        var first = (await AcceptAsyncCore(Request())).Value;
        var second = (await AcceptAsyncCore(Request() with { TitleId = 2 })).Value;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<int> PublishTogether(ArtworkImportJob job)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
                ready.SetResult();
            await ready.Task;
            return await PublishAsync(job, Content(1));
        }

        var ids = await Task.WhenAll(PublishTogether(first), PublishTogether(second));

        ids.Distinct().Count().ShouldBe(2);
        await using var db = _database.CreateContext();
        (await db.Files.CountAsync()).ShouldBe(SeededFiles);
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
        if (cancel)
            claim.Job.Cancel(cancelledAt: DateTimeOffset.UtcNow);
        else
            claim.Job.Fail("Invalid image.", failedAt: DateTimeOffset.UtcNow);

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
        pending.Cancel(cancelledAt: DateTimeOffset.UtcNow);
        await using var db = _database.CreateContext();

        await Should.ThrowAsync<InvalidOperationException>(() => new ArtworkImportJobRepository(db, TimeProvider.System)
            .UpdateStagedAsync(pending));

        (await db.Jobs.SingleAsync()).Phase.ShouldBe("Pending");
        (await db.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(pending.Id);
    }

    [Fact]
    public async Task TerminalCheckpoint_RolledBack_RevertsJobAndPendingCleanupTogether()
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        var claim = await ClaimAsync(pending.Id);
        claim.Job.Cancel(cancelledAt: DateTimeOffset.UtcNow);
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateStagedAsync(claim.Job);
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
        pending.Cancel(cancelledAt: DateTimeOffset.UtcNow);
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            await new JobRepository(db, TimeProvider.System).UpdateStagedAsync(pending);
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
        claim.Job.RecordError("attempt", "Commit acknowledgement was interrupted.", occurredAt: DateTimeOffset.UtcNow);
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
        pending.Start("delivery-1", startedAt: DateTimeOffset.UtcNow);
        await using (var db = _database.CreateContext())
            await new ArtworkImportJobRepository(db, TimeProvider.System).CommitCheckpointAsync(db, pending);
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

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private async Task<ErrorOr<ArtworkImportJob>> AcceptAsyncCore(ArtworkImportRequest request)
    {
        await using var db = _database.CreateContext();
        // A setup request imports under the role's current revision; a precondition test names its own.
        if (ReferenceEquals(request.IfMatch, AtCurrentRevision))
        {
            var current = (await Repository(db).GetStatesAsync(request.TitleId, default)).Value
                .Single(state => state.Role == request.Role).Revision;
            request = request with { IfMatch = Role(current) };
        }
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
        if (!complete)
            return assetId;
        claim.Job.SetImportOutcome(assetId, false);
        claim.Job.Complete(completedAt: DateTimeOffset.UtcNow);
        await using var checkpoint = _database.CreateContext();
        (await Claims(checkpoint).TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeTrue();
        return assetId;
    }

    private static ArtworkCurationRepository Repository(RomdDbContext db) => new(db, TimeProvider.System);
    private static ArtworkCurationService Service(RomdDbContext db) => new(Repository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtworkCurationService>.Instance);
    private static ClaimedJobRepository<ArtworkImportJob, ArtworkImportJobRepository> Claims(RomdDbContext db) =>
        new(new ArtworkImportJobRepository(db, TimeProvider.System), db, TimeProvider.System, new JobExecutionClaimOptions());
    private static readonly RevisionPrecondition AtCurrentRevision = new(["current"]);
    private static RevisionPrecondition Role(long revision) => RevisionPrecondition.For(ArtworkValidators.Role(revision));
    private static RevisionPrecondition Family(string revision) => RevisionPrecondition.For(revision);
    private static async Task<RevisionPrecondition> FamilyAsync(ArtworkCurationRepository repository) =>
        Family((await repository.GetFamilyAsync(1, default)).Value.Revision);
    private static ArtworkImportRequest Request() => new(Guid.NewGuid(), 1, ArtworkRole.Poster, "steamgriddb",
        "game", "asset", "https://cdn2.steamgriddb.com/grid/asset.png", "Artist", null, AtCurrentRevision);
    /// <summary>Artwork whose files the ledger recorded in <see cref="SeedAsync" />: an original and one variant.</summary>
    private RetainedArtworkContent Content(byte hash) => new(
        new RetainedArtworkFile(_files[Hash(hash)], Hash(hash), "image/png", 600, 900, "original"),
        [new RetainedArtworkFile(_files[Hash((byte)(hash + 1))], Hash((byte)(hash + 1)), "image/webp", 300, 450, "card")],
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
            Id = 1,
            Name = "Test platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test platform",
            BaseCompactLabel = "Test platform",
            CanonicalKey = "test",
            ShortName = "test",
            Manufacturer = "Test",
            CreatedAt = now,
            CreatedByUserId = Guid.Empty
        });
        for (var id = 1; id <= 2; id++)
            db.Titles.Add(new TitleEntity
            {
                Id = id,
                PlatformId = 1,
                Name = $"Title {id}",
                NormalizedName = $"title {id}",
                EnrichmentStatus = "None",
                FieldProvenanceJson = "{}",
                FieldSourceOverridesJson = "{}",
                ScreenshotPrefsJson = "{}",
                CreatedAt = now,
                CreatedByUserId = Guid.Empty
            });
        await db.SaveChangesAsync();
        // Publication records artwork files through the ledger before the repository stages an asset.
        var files = new FileRepository(db);
        for (byte hash = 1; hash <= SeededFiles; hash++)
            _files[Hash(hash)] = (await files.CreateAndCommitAsync(db, FileEntity.CreateNew(Hash(hash), 100, 100, false, now: DateTimeOffset.UnixEpoch), CancellationToken.None)).Id;
    }
}
