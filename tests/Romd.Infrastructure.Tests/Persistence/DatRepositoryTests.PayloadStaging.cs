using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class DatRepositoryTests
{
    [Fact]
    public async Task AddGamesBatchStaged_OwnsGraphAndOrderedIdentities_WithoutFlushingOrClearingCallerStaging()
    {
        await using var db = CreateDb();
        await SeedDatWithFileAsync(db, 7, 5, 10, 10);
        int entryId = await SeedSourceEntryAsync(db, 7, "Shared entry");
        var first = DatGame.CreateNew(7, "Repeated", description: "first");
        first.AddRom(DatRom.CreateNew("first.rom", 1, sha1: NewSha1(1)));
        first.AddDisk(DatDisk.CreateNew("first.disk", NewSha1(2)));
        var second = DatGame.CreateNew(7, "Repeated", description: "second");
        var input = new List<DatGameWithEntry> { new(first, entryId), new(second, entryId) };
        var work = new EfUnitOfWork(db);
        await using var tx = await work.BeginTransactionAsync();
        var unrelated = new RegionEntity { Name = "Pending", CanonicalKey = "dat-pending" };
        db.Regions.Add(unrelated);
        var repository = new DatRepository(db, TimeProvider.System);

        var staged = await repository.AddGamesBatchStagedAsync(input);
        input.Clear();
        first.AddRom(DatRom.CreateNew("added-after-staging.rom", 2));

        Should.Throw<InvalidOperationException>(() => staged.ReadIdsAfterFlush());
        (await db.DatGames.CountAsync()).ShouldBe(0);
        (await db.Regions.CountAsync()).ShouldBe(0);
        db.Entry(unrelated).State.ShouldBe(EntityState.Added);
        await work.FlushAsync();
        var ids = staged.ReadIdsAfterFlush();
        ids.Count.ShouldBe(2);
        ids.ShouldAllBe(id => id > 0);
        ids[0].ShouldNotBe(ids[1]);
        (await db.DatGames.SingleAsync(row => row.Id == ids[0])).Description.ShouldBe("first");
        (await db.DatGames.SingleAsync(row => row.Id == ids[1])).Description.ShouldBe("second");
        (await db.DatRoms.SingleAsync()).DatGameId.ShouldBe(ids[0]);
        (await db.DatDisks.SingleAsync()).DatGameId.ShouldBe(ids[0]);
        if (ids is IList<int> mutable)
        {
            try
            { mutable[0] = -1; }
            catch (NotSupportedException) { }
        }
        staged.ReadIdsAfterFlush()[0].ShouldBeGreaterThan(0);
        await using (var outside = CreateDb())
            (await outside.DatGames.CountAsync()).ShouldBe(0);
        await tx.CommitAsync();
        Should.Throw<InvalidOperationException>(() => staged.ReadIdsAfterFlush());
    }

    [Fact]
    public async Task PayloadStaging_EmptyInputsAreNoOps_WhileNonemptyMisuseRejectsBeforeTracking()
    {
        await using var db = CreateDb();
        var repository = new DatRepository(db, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        (await repository.AddGamesBatchStagedAsync([], cancellation.Token)).ReadIdsAfterFlush().ShouldBeEmpty();
        await repository.AddGameRegionsBatchStagedAsync([], cancellation.Token);
        await repository.AddGameLanguagesBatchStagedAsync([], cancellation.Token);
        (await repository.LinkDatRomsToExistingRomFilesStagedAsync([], cancellation.Token)).ShouldBe(0);
        var game = new DatGameWithEntry(DatGame.CreateNew(7, "New"), 7);
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddGamesBatchStagedAsync([game]));
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddGameRegionsBatchStagedAsync([(1, 1)]));
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddGameLanguagesBatchStagedAsync([(1, 1)]));
        await Should.ThrowAsync<InvalidOperationException>(() => repository.LinkDatRomsToExistingRomFilesStagedAsync([1]));
        (await Should.ThrowAsync<OperationCanceledException>(() => repository.AddGamesBatchStagedAsync([game], cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        (await Should.ThrowAsync<OperationCanceledException>(() => repository.AddGameRegionsBatchStagedAsync([(1, 1)], cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        (await Should.ThrowAsync<OperationCanceledException>(() => repository.AddGameLanguagesBatchStagedAsync([(1, 1)], cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        (await Should.ThrowAsync<OperationCanceledException>(() => repository.LinkDatRomsToExistingRomFilesStagedAsync([1], cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        var work = new EfUnitOfWork(db);
        await using var tx = await work.BeginTransactionAsync();
        var identified = new DatGameEntity { Id = 99, DatFileId = 7, Name = "Existing" }.ToDomain();
        await Should.ThrowAsync<ArgumentException>(() => repository.AddGamesBatchStagedAsync([game, new(identified, 7)]));
        (await repository.LinkDatRomsToExistingRomFilesStagedAsync([99])).ShouldBe(0);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AddGamesBatchStaged_FlushedRollbackAndSameContextRetry_RejectsOldReceiptAndUsesOwnIds()
    {
        await using var db = CreateDb();
        await SeedDatWithFileAsync(db, 7, 5, 10, 10);
        int entryId = await SeedSourceEntryAsync(db, 7, "Retry");
        var repository = new DatRepository(db, TimeProvider.System);
        var work = new EfUnitOfWork(db);
        IStagedDatGameBatch failed;
        int failedId;
        await using (var tx = await work.BeginTransactionAsync())
        {
            failed = await repository.AddGamesBatchStagedAsync([new(DatGame.CreateNew(7, "Retry"), entryId)]);
            await work.FlushAsync();
            failedId = failed.ReadIdsAfterFlush().Single();
        }
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var retry = await work.BeginTransactionAsync();
        Should.Throw<InvalidOperationException>(() => failed.ReadIdsAfterFlush());
        var staged = await repository.AddGamesBatchStagedAsync([new(DatGame.CreateNew(7, "Retry"), entryId)]);
        await work.FlushAsync();
        int actualId = staged.ReadIdsAfterFlush().Single();
        actualId.ShouldNotBe(failedId);
        await retry.CommitAsync();
        await using var verify = CreateDb();
        (await verify.DatGames.SingleAsync()).Id.ShouldBe(actualId);
    }

    [Fact]
    public async Task LinkExistingRomStaged_RepeatedPendingLinksAndDuplicateGameIds_DoNotRelinkOrFlush()
    {
        await using var db = CreateDb();
        await SeedRomLinkDataAsync(db);
        var work = new EfUnitOfWork(db);
        await using var tx = await work.BeginTransactionAsync();
        var repository = new DatRepository(db, TimeProvider.System);
        var unrelated = new RegionEntity { Name = "Pending", CanonicalKey = "dat-pending" };
        db.Regions.Add(unrelated);

        (await repository.LinkDatRomsToExistingRomFilesStagedAsync([1, 1])).ShouldBe(1);
        (await repository.LinkDatRomsToExistingRomFilesStagedAsync([1])).ShouldBe(0);

        (await db.DatRoms.AsNoTracking().SingleAsync()).RomFileId.ShouldBeNull();
        db.ChangeTracker.Entries<DatRomEntity>().Single().Entity.RomFileId.ShouldBe(1);
        db.Entry(unrelated).State.ShouldBe(EntityState.Added);
        (await db.Regions.CountAsync()).ShouldBe(0);
        await work.FlushAsync();
        (await repository.LinkDatRomsToExistingRomFilesStagedAsync([1])).ShouldBe(0);
        (await db.DatRoms.AsNoTracking().SingleAsync()).RomFileId.ShouldBe(1);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task JunctionsStaged_KeepBothPhasesPendingUntilOwnerFlush_AndRollbackTogether()
    {
        await using var db = CreateDb();
        await SeedRomLinkDataAsync(db);
        db.Regions.Add(new RegionEntity { Id = 7, Name = "USA", CanonicalKey = "dat-usa" });
        db.GameLanguages.Add(new GameLanguageEntity { Id = 8, Name = "English", Code = "en", CanonicalKey = "dat-english" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var work = new EfUnitOfWork(db);
        await using (var tx = await work.BeginTransactionAsync())
        {
            var repository = new DatRepository(db, TimeProvider.System);
            await repository.AddGameRegionsBatchStagedAsync([(1, 7)]);
            await repository.AddGameLanguagesBatchStagedAsync([(1, 8)]);
            (await db.DatGameRegions.CountAsync()).ShouldBe(0);
            (await db.DatGameLanguages.CountAsync()).ShouldBe(0);
            db.ChangeTracker.Entries<DatGameRegionEntity>().Single().State.ShouldBe(EntityState.Added);
            db.ChangeTracker.Entries<DatGameLanguageEntity>().Single().State.ShouldBe(EntityState.Added);
            await work.FlushAsync();
            (await db.DatGameRegions.CountAsync()).ShouldBe(1);
            (await db.DatGameLanguages.CountAsync()).ShouldBe(1);
        }
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verification = CreateDb();
        (await verification.DatGameRegions.CountAsync()).ShouldBe(0);
        (await verification.DatGameLanguages.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionsStaged_DuplicateKeysStillReject_InsteadOfDeduplicating(bool language)
    {
        await using var db = CreateDb();
        var work = new EfUnitOfWork(db);
        await using var tx = await work.BeginTransactionAsync();
        var repository = new DatRepository(db, TimeProvider.System);

        await Should.ThrowAsync<InvalidOperationException>(() => language
            ? repository.AddGameLanguagesBatchStagedAsync([(1, 8), (1, 8)])
            : repository.AddGameRegionsBatchStagedAsync([(1, 7), (1, 7)]));
    }

    [Fact]
    public async Task LinkExistingRomStaged_AmbiguousSha1_RejectsWithoutInventingAWinner()
    {
        await using var db = CreateDb();
        await SeedRomLinkDataAsync(db);
        db.Files.Add(new FileEntityPersistence { Id = 2, Sha256 = NewSha256(2), Size = 1, SizeOnDisk = 1, CreatedAt = DateTimeOffset.UtcNow });
        db.StageOriginalRom(new OriginalRomFixture
        {
            Id = 2,
            FileId = 2,
            OriginalFilename = "collision.rom",
            Sha1 = NewSha1(1),
            Md5 = NewMd5(2),
            Crc32 = Romd.Domain.Hashing.Crc32.FromUInt32(2),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var work = new EfUnitOfWork(db);
        await using var tx = await work.BeginTransactionAsync();
        var repository = new DatRepository(db, TimeProvider.System);

        await Should.ThrowAsync<ArgumentException>(() => repository.LinkDatRomsToExistingRomFilesStagedAsync([1]));

        (await db.DatRoms.SingleAsync()).RomFileId.ShouldBeNull();
        db.ChangeTracker.Entries<DatRomEntity>().ShouldBeEmpty();
    }
}
