using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.BackfillBiosCatalog;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class BiosRepositoryTests
{
    [Fact]
    public async Task BackfillBiosCatalog_CreatesAndMapsMissingFirmware_ThenHasNoWork()
    {
        await using var db = CreateDb();
        SeedOwnershipGraph(db);
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        await db.BiosGameMappings.ExecuteDeleteAsync();
        await db.Bios.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        var work = new EfUnitOfWork(db);
        var repository = new BiosRepository(db);
        var handler = new BackfillBiosCatalogCommandHandler(repository,
            new BiosGrouper(repository, work, TimeProvider.System), work,
            NullLogger<BackfillBiosCatalogCommandHandler>.Instance);

        var first = await handler.HandleAsync(new BackfillBiosCatalogCommand());
        var second = await handler.HandleAsync(new BackfillBiosCatalogCommand());

        first.IsError.ShouldBeFalse();
        first.Value.BiosEntriesCreated.ShouldBe(3);
        first.Value.GamesGrouped.ShouldBe(3);
        second.IsError.ShouldBeFalse();
        second.Value.BiosEntriesCreated.ShouldBe(0);
        second.Value.GamesGrouped.ShouldBe(0);
        await using var verification = CreateDb();
        (await verification.Bios.OrderBy(row => row.NormalizedName).Select(row => row.NormalizedName).ToListAsync())
            .ShouldBe(["psxeurope", "psxusav1", "psxusav2"]);
        (await verification.BiosGameMappings.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task StagedWrites_EmptyInput_IsANoOpWithoutTransactionEvenWhenCancelled()
    {
        await using var db = CreateDb();
        var repository = new BiosRepository(db);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        (await repository.AddRangeStagedAsync([], cancellation.Token)).ReadAfterFlush().ShouldBeEmpty();
        await repository.AddGameMappingsStagedAsync([], cancellation.Token);

        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task StagedWrites_MissingTransactionOrExistingIdentity_RejectBeforeTracking()
    {
        await using var db = CreateDb();
        var repository = new BiosRepository(db);
        var bios = Bios.CreateNew(1, "New", "new", DateTimeOffset.UnixEpoch);
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddRangeStagedAsync([bios]));
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddGameMappingsStagedAsync([(1, 1)]));
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();

        var error = await Should.ThrowAsync<ArgumentException>(() => repository.AddRangeStagedAsync([
            bios, Bios.Rehydrate(42, 1, "Existing", "existing", DateTimeOffset.UnixEpoch)]));

        error.ParamName.ShouldBe("bios");
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AddRangeStagedAsync_CallerMutatesInput_ReceiptKeepsOwnInsertionOrderAndRollback()
    {
        await using var db = CreateDb();
        var repository = new BiosRepository(db);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var createdAt = DateTimeOffset.UnixEpoch.AddTicks(1234567);
        var input = new List<Bios>
        {
            Bios.CreateNew(1, "USA", "usa", createdAt), Bios.CreateNew(1, "Japan", "japan", createdAt)
        };
        var receipt = await repository.AddRangeStagedAsync(input);
        input.Clear();
        Should.Throw<InvalidOperationException>(() => receipt.ReadAfterFlush());

        await work.FlushAsync();

        var created = receipt.ReadAfterFlush();
        created.Select(entry => entry.Name).ShouldBe(["USA", "Japan"]);
        created.Select(entry => entry.Id).Distinct().Count().ShouldBe(2);
        created.ShouldAllBe(entry => entry.Id > 0 && entry.CreatedAt == createdAt);
        await using (var outside = CreateDb())
            (await outside.Bios.CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
        await work.FlushAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verification = CreateDb();
        (await verification.Bios.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AddGameMappingsStagedAsync_CallerFlushThenRollback_PreservesOtherTrackedStateAndDiscardsMappings()
    {
        await using var db = CreateDb();
        SeedOwnershipGraph(db);
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        await db.BiosGameMappings.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var platform = await db.Platforms.AsTracking().SingleAsync(row => row.Id == 1);
        platform.Name = "Before mapping";

        await new BiosRepository(db).AddGameMappingsStagedAsync([(1, 1), (2, 1)]);

        (await db.BiosGameMappings.CountAsync()).ShouldBe(0);
        platform.Name = "After mapping";
        await work.FlushAsync();
        (await db.BiosGameMappings.CountAsync()).ShouldBe(2);
        (await db.Platforms.AsNoTracking().SingleAsync(row => row.Id == 1)).Name.ShouldBe("After mapping");
        await transaction.RollbackAsync();
        await work.FlushAsync();
        await using var verification = CreateDb();
        (await verification.BiosGameMappings.CountAsync()).ShouldBe(0);
        (await verification.Platforms.SingleAsync(row => row.Id == 1)).Name.ShouldBe("Sony PlayStation");
    }
}
