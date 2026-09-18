using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Collections.Commands.UpdateCollection;
using Romd.Consumer.Application.Browse;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class CollectionDetailsMutationTests : IDisposable
{
    private static readonly DateTimeOffset AddedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task UpdateCollection_PopulatedCollection_PreservesItemsAndReturnsAccurateCount()
    {
        await SeedAsync();
        await using var context = CreateContext();
        var handler = new UpdateCollectionCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            CreateRepository(context), new EfUnitOfWork(context),
            NullLogger<UpdateCollectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UpdateCollectionCommand(
            30, " Renamed ", " New description ", null, 10));

        result.IsError.ShouldBeFalse();
        result.Value.Name.ShouldBe("Renamed");
        result.Value.ItemCount.ShouldBe(2);
        await using var assertion = CreateContext();
        var collection = await assertion.Collections.Include(c => c.Items).SingleAsync();
        collection.Name.ShouldBe("Renamed");
        collection.Description.ShouldBe("New description");
        collection.PlatformId.ShouldBe(10);
        collection.IsSystem.ShouldBeTrue();
        collection.SortOrder.ShouldBe(7);
        collection.CreatedAt.ShouldBe(AddedAt);
        AssertItems(collection);
    }

    [Fact]
    public async Task UpdateDetailsStagedAsync_BeforeCommit_DoesNotPersistAndRollbackDiscardsChanges()
    {
        await SeedAsync();
        await using var context = CreateContext();
        var repository = CreateRepository(context);
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        var collection = await repository.GetByIdAsync(30);
        collection.ShouldNotBeNull();
        collection.Items.ShouldBeEmpty();
        collection.UpdateDetails("Rolled back", null, null, null);

        var summary = await repository.UpdateDetailsStagedAsync(collection);
        summary.ShouldNotBeNull();
        summary.ItemCount.ShouldBe(2);
        await using (var beforeCommit = CreateContext())
        {
            (await beforeCommit.Collections.SingleAsync()).Name.ShouldBe("Original");
        }
        await transaction.RollbackAsync();
        // Rolling back must also discard staged tracked changes, preventing a later flush from leaking them.
        await context.SaveChangesAsync();

        await using var assertion = CreateContext();
        var persisted = await assertion.Collections.Include(c => c.Items).SingleAsync();
        persisted.Name.ShouldBe("Original");
        AssertItems(persisted);
    }

    [Fact]
    public async Task UpdateCollection_MissingCollection_ReturnsNotFound()
    {
        await using var context = CreateContext();
        var handler = new UpdateCollectionCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            CreateRepository(context), new EfUnitOfWork(context),
            NullLogger<UpdateCollectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UpdateCollectionCommand(999, "Missing", null, null, null));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound);
    }

    private async Task SeedAsync()
    {
        await using var context = CreateContext();
        context.Platforms.Add(new PlatformEntity
        {
            Id = 10, Name = "NES", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "NES", BaseCompactLabel = "NES", CanonicalKey = "nes", ShortName = "nes", CreatedAt = AddedAt
        });
        context.Titles.AddRange(
            new TitleEntity
            {
                Id = 20, PlatformId = 10, Name = "First", NormalizedName = "first",
                EnrichmentStatus = "NotEnriched", CreatedAt = AddedAt
            },
            new TitleEntity
            {
                Id = 21, PlatformId = 10, Name = "Second", NormalizedName = "second",
                EnrichmentStatus = "NotEnriched", CreatedAt = AddedAt
            });
        context.Collections.Add(new CollectionEntity
        {
            Id = 30, Name = "Original", Description = "Original description", IsSystem = true,
            SortOrder = 7, CreatedAt = AddedAt,
            Items =
            [
                new CollectionItemEntity
                {
                    Id = 40, CollectionId = 30, TitleId = 21, SortOrder = 0,
                    Note = "Play first", AddedAt = AddedAt, CreatedAt = AddedAt
                },
                new CollectionItemEntity
                {
                    Id = 41, CollectionId = 30, TitleId = 20, SortOrder = 1,
                    Note = "Play next", AddedAt = AddedAt.AddDays(1), CreatedAt = AddedAt
                }
            ]
        });
        await context.SaveChangesAsync();
    }

    private static void AssertItems(CollectionEntity collection)
    {
        var items = collection.Items.OrderBy(item => item.SortOrder).ToList();
        items.Select(item => item.Id).ShouldBe([40, 41]);
        items.Select(item => item.TitleId).ShouldBe([21, 20]);
        items.Select(item => item.SortOrder).ShouldBe([0, 1]);
        items.Select(item => item.Note).ShouldBe(["Play first", "Play next"]);
        items.Select(item => item.AddedAt).ShouldBe([AddedAt, AddedAt.AddDays(1)]);
    }

    private RomdDbContext CreateContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql(_database.ConnectionString).Options);

    private static CollectionRepository CreateRepository(RomdDbContext context) =>
        new(context, Substitute.For<IConsumerReleaseSelector>());

    public void Dispose() => _database.Dispose();
}
