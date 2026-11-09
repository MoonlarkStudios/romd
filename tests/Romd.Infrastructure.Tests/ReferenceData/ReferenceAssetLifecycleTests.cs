using Romd.Domain.ReferenceData;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.ReferenceData.Assets.Commands.UploadReferenceAsset;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;

using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed partial class ReferenceAssetLifecycleTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"romd-reference-cas-{Guid.NewGuid():N}");
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
    private static FileStorageService Ledger(RomdDbContext db, IContentAddressableStore cas) => new(cas,
        new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db), new AdminRealtimeOutbox(db, TimeProvider.System),
        NullLogger<FileStorageService>.Instance, TimeProvider.System);
    private IContentAddressableStore Store() => new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = _root).BuildServiceProvider().GetRequiredService<IContentAddressableStore>();

    [Fact]
    public async Task Upload_ReservationProtectsCas_UntilExpiryThenExistingCollectorDeletesIt()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var service = new ReferenceCatalogService(db, new ReferenceBlobStore(cas, Ledger(db, cas)));
        var hash = (await UploadAsync(db, Png, default)).Value;
        var file = await db.Files.SingleAsync(x => x.Sha256 == Sha256.Parse(hash));
        var repository = new FileRepository(db);
        (await repository.IsReferencedAsync(file.Id, default)).ShouldBeTrue();
        (await repository.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(1), default)).ShouldBeEmpty();
        (await repository.GetStorageStatsAsync(default)).Breakdown.Single(x => x.Category == "media").FileCount.ShouldBe(1);
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(0);
        (await service.GetAssetAsync(hash, default))!.Bytes.ShouldBe(Png);

        await db.ReferenceAssets.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedUntil, DateTimeOffset.UtcNow.AddDays(-1)));
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(1);
        db.ChangeTracker.Clear();
        var cleanup = new FileStorageService(cas, repository, new EfUnitOfWork(db), new FileMutationLock(db),
            new AdminRealtimeOutbox(db, TimeProvider.System), NullLogger<FileStorageService>.Instance, clock: TimeProvider.System);
        await cleanup.DeleteIfUnreferencedAsync(file.Id, default);
        (await db.Files.AnyAsync(x => x.Id == file.Id)).ShouldBeFalse();
        (await new ReadOnlyReferenceBlobStore(cas).ReadAsync(hash, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Override_PreservesHiddenDefault_AndReleaseStartsFreshGracePeriod()
    {
        await using var db = _database.CreateContext();
        var service = new ReferenceCatalogService(db, TestReferenceBlobs.For(db));
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var original = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        var oldRevision = (await service.GetCurrentAsync(default))!.Revision;
        var hash = (await UploadAsync(db, Png, default)).Value;
        var edited = await new TypedReferenceTestCommands(db).UpdateSystem("snes", new SystemPatch { Icon = hash }, original.Revision, default);
        edited.IsError.ShouldBeFalse();
        (await service.GetCurrentAsync(default))!.Revision.ShouldNotBe(oldRevision);
        (await db.ReferenceCatalogState.CountAsync()).ShouldBe(1);
        (await db.ReferenceAssetOwners.Where(x => x.Kind == "systems" && x.Key == "snes").CountAsync()).ShouldBe(2);
        await db.ReferenceAssets.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedUntil, DateTimeOffset.UtcNow.AddDays(-20)));
        db.ChangeTracker.Clear();
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(0);
        (await service.GetAssetAsync(original.Resource.Icon!.Sha256, default)).ShouldNotBeNull();
        var reset = await new TypedReferenceTestCommands(db).ResetSystem("snes", null, edited.Value.Revision, default);
        reset.IsError.ShouldBeFalse();
        reset.Value.Resource.Icon.ShouldBe(original.Resource.Icon);
        (await db.ReferenceAssets.SingleAsync(x => x.Hash == hash)).ReservedUntil.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(6));
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(0);
        // Attaching an old upload again protects it even beyond its reservation.
        (await new TypedReferenceTestCommands(db).UpdateSystem("snes", new SystemPatch { Icon = hash }, reset.Value.Revision, default)).IsError.ShouldBeFalse();
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow.AddDays(30), default)).ShouldBe(0);
    }

    [Fact]
    public async Task Upload_DeduplicatesExistingFile_AndRenewalProtectsExpiredReservation()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var service = new ReferenceCatalogService(db, new ReferenceBlobStore(cas, Ledger(db, cas)));
        var hash = (await UploadAsync(db, Png, default)).Value;
        await db.ReferenceAssets.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedUntil, DateTimeOffset.UtcNow.AddDays(-1)));
        db.ChangeTracker.Clear();
        (await UploadAsync(db, Png, default)).Value.ShouldBe(hash);
        (await db.Files.CountAsync()).ShouldBe(1);
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(0);
    }

    [Fact]
    public async Task CatalogPublication_RepeatedHashBeforeCommit_CreatesOneReservation()
    {
        await using var db = _database.CreateContext();
        var files = Ledger(db, Store());
        using var firstBytes = new MemoryStream(Png, writable: false);
        using var secondBytes = new MemoryStream(Png, writable: false);
        await using var first = await files.StageAsync(firstBytes);
        await using var second = await files.StageAsync(secondBytes);
        var work = new EfUnitOfWork(db);
        await using var tx = await work.BeginTransactionAsync();
        var reservations = new ReferenceAssetReservationStore(db);
        await reservations.AcquireAsync(default);
        var publication = await files.PublishStagedAsync([first, second]);
        publication.RequiresFlush.ShouldBeTrue();
        await work.FlushAsync();
        var results = publication.ReadAfterFlush();
        results[0].File.Id.ShouldBe(results[1].File.Id);
        var until = DateTimeOffset.UtcNow.AddDays(7);
        foreach (var result in results)
            await reservations.ReserveStagedAsync(result.File.Sha256, result.File.Id, "image/png", until, default);
        (await db.ReferenceAssets.CountAsync()).ShouldBe(0);
        await tx.CommitAsync();
        (await db.ReferenceAssets.CountAsync()).ShouldBe(1);
        (await db.Files.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredReference_SharedWithDatCandidate_RemainsOwnedByDat()
    {
        await using var db = _database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var service = new ReferenceCatalogService(db, TestReferenceBlobs.For(db));
        var hash = (await UploadAsync(db, Png, default)).Value;
        var file = await db.Files.SingleAsync(x => x.Sha256 == Sha256.Parse(hash));
        db.DatSubscriptions.Add(new DatSubscriptionEntity { CatalogId = "test/shared", PlatformId = (await db.Platforms.FirstAsync()).Id, CandidateFileId = file.Id, State = "ReadyToImport" });
        await db.SaveChangesAsync();
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow.AddDays(8), default)).ShouldBe(1);
        var repository = new FileRepository(db);
        (await repository.IsReferencedAsync(file.Id, default)).ShouldBeTrue();
        (await repository.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(30), default)).ShouldNotContain(file.Id);
    }

    [Fact]
    public async Task Cleanup_WaitsForPublication_AndRechecksCommittedOwnership()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var publisher = _database.CreateContext();
        var service = new ReferenceCatalogService(publisher, TestReferenceBlobs.For(publisher));
        var hash = (await UploadAsync(publisher, Png, ct)).Value;
        await using var tx = await publisher.Database.BeginTransactionAsync(ct);
        await ReferenceCatalogService.LockAsync(publisher, ct);
        await using var collector = _database.CreateContext();
        var cleanup = ReferenceCatalogService.ReleaseExpiredAssetsAsync(collector, DateTimeOffset.UtcNow.AddDays(8), ct);
        publisher.ReferenceAssetOwners.Add(new ReferenceAssetOwnerEntity { Kind = "systems", Key = "local-inflight", Slot = "base", Hash = hash });
        await publisher.SaveChangesAsync(ct);
        cleanup.IsCompleted.ShouldBeFalse();
        await tx.CommitAsync(ct);
        (await cleanup).ShouldBe(0);
    }

    [Fact]
    public async Task FreshSchema_SeedsFinalCasCatalog_IdempotentlyWithoutHistoricalStorage()
    {
        await using var db = _database.CreateContext();
        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        var cas = Store();
        var blobs = new ReferenceBlobStore(cas, Ledger(db, cas));
        await SharedReferenceDataSeeder.InitializeAsync(db, blobs, clock: TimeProvider.System);
        var service = new ReferenceCatalogService(db, blobs);
        var first = (await service.GetCurrentAsync(default))!;
        var fileCount = await db.Files.CountAsync();
        await SharedReferenceDataSeeder.InitializeAsync(db, blobs, clock: TimeProvider.System);
        (await service.GetCurrentAsync(default))!.Revision.ShouldBe(first.Revision);
        (await db.Files.CountAsync()).ShouldBe(fileCount);
        (await db.ReferenceCatalogState.CountAsync()).ShouldBe(1);
        var icon = first.Systems.Single(x => x.Key == "snes").Icon!;
        ReferenceCatalogService.Hash((await service.GetAssetAsync(icon.Sha256, default))!.Bytes).ShouldBe(icon.Sha256);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.tables WHERE table_schema = 'romd' AND table_name = 'ReferenceSnapshots'").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_schema = 'romd' AND table_name = 'ReferenceAssets' AND column_name = 'Bytes'").SingleAsync()).ShouldBe(0);
    }

    private Task<ErrorOr<string>> UploadAsync(RomdDbContext db, byte[] bytes, CancellationToken ct) =>
        new UploadReferenceAssetCommandHandler(new ReferenceAssetReservationStore(db), Ledger(db, Store()),
            new EfUnitOfWork(db), TimeProvider.System, NullLogger<UploadReferenceAssetCommandHandler>.Instance)
            .HandleAsync(new UploadReferenceAssetCommand(bytes, "image/png"), ct);

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
