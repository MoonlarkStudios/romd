using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

public sealed class ReferenceAssetLifecycleTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"romd-reference-cas-{Guid.NewGuid():N}");
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
    private IContentAddressableStore Store() => new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = _root).BuildServiceProvider().GetRequiredService<IContentAddressableStore>();

    [Fact]
    public async Task Upload_ReservationProtectsCas_UntilExpiryThenExistingCollectorDeletesIt()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var service = new ReferenceCatalogService(db, new ReferenceBlobStore(cas));
        var hash = (await service.AddAssetAsync(Png, "image/png", default)).Value;
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
            new AdminRealtimeOutbox(db, TimeProvider.System), Options.Create(new ContentStoreOptions { RootPath = _root }), NullLogger<FileStorageService>.Instance);
        await cleanup.DeleteIfUnreferencedAsync(file.Id, default);
        (await db.Files.AnyAsync(x => x.Id == file.Id)).ShouldBeFalse();
        (await new ReferenceBlobStore(cas).ReadAsync(hash, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Override_PreservesHiddenDefault_AndReleaseStartsFreshGracePeriod()
    {
        await using var db = _database.CreateContext();
        var service = new ReferenceCatalogService(db, TestReferenceBlobs.Instance);
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var original = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        var oldRevision = (await service.GetCurrentAsync(default))!.Revision;
        var hash = (await service.AddAssetAsync(Png, "image/png", default)).Value;
        var edited = await new TypedReferenceTestCommands(db).UpdateSystem("snes", new SystemPatchDto { Icon = hash }, original.ETag, default);
        edited.IsError.ShouldBeFalse();
        (await service.GetCurrentAsync(default))!.Revision.ShouldNotBe(oldRevision);
        (await db.ReferenceCatalogState.CountAsync()).ShouldBe(1);
        (await db.ReferenceAssetOwners.Where(x => x.Kind == "systems" && x.Key == "snes").CountAsync()).ShouldBe(2);
        await db.ReferenceAssets.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedUntil, DateTimeOffset.UtcNow.AddDays(-20)));
        db.ChangeTracker.Clear();
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(0);
        (await service.GetAssetAsync(original.Resource.Icon!.Sha256, default)).ShouldNotBeNull();
        var reset = await new TypedReferenceTestCommands(db).ResetSystem("snes", null, edited.Value.ETag, default);
        reset.IsError.ShouldBeFalse();
        reset.Value.Resource.Icon.ShouldBe(original.Resource.Icon);
        (await db.ReferenceAssets.SingleAsync(x => x.Hash == hash)).ReservedUntil.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(6));
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(0);
        // Attaching an old upload again protects it even beyond its reservation.
        (await new TypedReferenceTestCommands(db).UpdateSystem("snes", new SystemPatchDto { Icon = hash }, reset.Value.ETag, default)).IsError.ShouldBeFalse();
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow.AddDays(30), default)).ShouldBe(0);
    }

    [Fact]
    public async Task Upload_DeduplicatesExistingFile_AndRenewalProtectsExpiredReservation()
    {
        await using var db = _database.CreateContext();
        var service = new ReferenceCatalogService(db, new ReferenceBlobStore(Store()));
        var hash = (await service.AddAssetAsync(Png, "image/png", default)).Value;
        await db.ReferenceAssets.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedUntil, DateTimeOffset.UtcNow.AddDays(-1)));
        db.ChangeTracker.Clear();
        (await service.AddAssetAsync(Png, "image/png", default)).Value.ShouldBe(hash);
        (await db.Files.CountAsync()).ShouldBe(1);
        (await ReferenceCatalogService.ReleaseExpiredAssetsAsync(db, DateTimeOffset.UtcNow, default)).ShouldBe(0);
    }

    [Fact]
    public async Task CatalogPublication_RepeatedHashBeforeCommit_CreatesOneReservation()
    {
        await using var db = _database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await ReferenceCatalogService.LockAsync(db, default);
        var first = await ReferenceCatalogService.StoreAssetAsync(db, TestReferenceBlobs.Instance, Png, "image/png", default);
        var second = await ReferenceCatalogService.StoreAssetAsync(db, TestReferenceBlobs.Instance, Png, "image/png", default);
        first.ShouldBe(second);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        (await db.ReferenceAssets.CountAsync()).ShouldBe(1);
        (await db.Files.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredReference_SharedWithDatCandidate_RemainsOwnedByDat()
    {
        await using var db = _database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, TestReferenceBlobs.Instance);
        var hash = (await service.AddAssetAsync(Png, "image/png", default)).Value;
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
        var service = new ReferenceCatalogService(publisher, TestReferenceBlobs.Instance);
        var hash = (await service.AddAssetAsync(Png, "image/png", ct)).Value;
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
        var blobs = new ReferenceBlobStore(Store());
        await SharedReferenceDataSeeder.InitializeAsync(db, blobs);
        var service = new ReferenceCatalogService(db, blobs);
        var first = (await service.GetCurrentAsync(default))!;
        var fileCount = await db.Files.CountAsync();
        await SharedReferenceDataSeeder.InitializeAsync(db, blobs);
        (await service.GetCurrentAsync(default))!.Revision.ShouldBe(first.Revision);
        (await db.Files.CountAsync()).ShouldBe(fileCount);
        (await db.ReferenceCatalogState.CountAsync()).ShouldBe(1);
        var icon = first.Systems.Single(x => x.Key == "snes").Icon!;
        ReferenceCatalogService.Hash((await service.GetAssetAsync(icon.Sha256, default))!.Bytes).ShouldBe(icon.Sha256);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.tables WHERE table_schema = 'romd' AND table_name = 'ReferenceSnapshots'").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_schema = 'romd' AND table_name = 'ReferenceAssets' AND column_name = 'Bytes'").SingleAsync()).ShouldBe(0);
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
