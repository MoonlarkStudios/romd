using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Domain.Catalog.Ratings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application.Libraries.Commands.SetLibraryAttachments;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class LibraryExperienceRepositoryTests : IAsyncDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private RomdDbContext CreateContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql(database.ConnectionString).UseOpenIddict().Options);

    [Fact]
    public async Task Attachments_SharedAcrossAudiences_FilterContentsAndRespectPlacement()
    {
        var seed = await SeedAsync();
        await SetAsync(seed.First, [(seed.Collection, false)]);
        await SetAsync(seed.Second, [(seed.Collection, true)]);
        await using var context = CreateContext();
        var repository = new LibraryExperienceRepository(context);
        var first = await repository.GetAttachmentsAsync(seed.First, default);
        first.Single().VisibleCount.ShouldBe(1);
        first.Single().TotalCount.ShouldBe(2);
        first.Single().LibraryCount.ShouldBe(2);
        first.Single().IsFeatured.ShouldBeFalse();
        var collectionIds = await repository.GetCollectionIdsByLibraryAsync(default);
        collectionIds.Count.ShouldBe(2);
        collectionIds[seed.First].ShouldBe([IdCoder.Encode(seed.Collection)]);
        collectionIds[seed.Second].ShouldBe([IdCoder.Encode(seed.Collection)]);
        (await repository.GetAttachmentsAsync(seed.Second, default)).Single().VisibleCount.ShouldBe(2);

        var preview = await repository.GetPreviewAsync(seed.First, seed.Collection, 0, int.MinValue, default);
        preview.Items.Select(t => t.Name).ShouldBe(["First Game"]);
        var secondPreview = await repository.GetPreviewAsync(seed.Second, seed.Collection, 0, int.MinValue, default);
        secondPreview.Items.Select(t => t.Name).ShouldBe(["Second Game", "First Game"]);

        var consumer = new CollectionRepository(context, new ConsumerReleaseSelector());
        var consumerRows = (await consumer.GetCollectionsAsync(new ConsumerLibraryScope(seed.User)))
            .ShouldBeOfType<ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionReadModel>>.Found>();
        consumerRows.Value.Single().IsFeatured.ShouldBeFalse();
        consumerRows.Value.Single().ItemCount.ShouldBe(preview.Items.Count);
        var libraryContext = (await new LibraryRepository(context).GetCurrentContextAsync(new ConsumerLibraryScope(seed.User)))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.Found>();
        libraryContext.Value.Counts.CollectionCount.ShouldBe(1);
        libraryContext.Value.FeaturedCollections.ShouldBeEmpty();
    }

    [Fact]
    public async Task Detach_RemovesConsumerVisibilityWithoutDeletingSharedCuration()
    {
        var seed = await SeedAsync();
        await SetAsync(seed.First, [(seed.Collection, true)]);
        await SetAsync(seed.Second, [(seed.Collection, true)]);
        await SetAsync(seed.First, []);
        await using var context = CreateContext();
        (await context.Collections.CountAsync()).ShouldBe(1);
        (await context.CollectionItems.CountAsync()).ShouldBe(2);
        (await context.LibraryCollections.CountAsync()).ShouldBe(1);
        var consumer = new CollectionRepository(context, new ConsumerReleaseSelector());
        (await consumer.GetCollectionByIdAsync(new ConsumerLibraryScope(seed.User), seed.Collection))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerCollectionReadModel>.ItemNotFound>();
        (await new LibraryExperienceRepository(context).GetPreviewAsync(seed.First, seed.Collection, 0, int.MinValue, default)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Replace_InvalidReferencesOrDuplicates_RetainsPreviousAttachments()
    {
        var seed = await SeedAsync();
        await SetAsync(seed.First, [(seed.Collection, true)]);
        foreach (var invalid in new IReadOnlyList<(int, bool)>[] { [(int.MaxValue, true)], [(seed.Collection, true), (seed.Collection, false)] })
        {
            await using var context = CreateContext();
            var handler = new SetLibraryAttachmentsCommandHandler(new LibraryExperienceRepository(context), new EfUnitOfWork(context));
            (await handler.HandleAsync(new(seed.First, invalid))).IsError.ShouldBeTrue();
        }
        await using var read = CreateContext();
        (await read.LibraryCollections.SingleAsync()).CollectionId.ShouldBe(seed.Collection);
    }

    [Fact]
    public async Task Attachments_EmptyOrInvalidAudience_RemainManageableButNotExposed()
    {
        var seed = await SeedAsync();
        await SetAsync(seed.First, [(seed.Collection, true)]);
        await using var context = CreateContext();
        await context.Libraries.Where(l => l.Id == seed.First).ExecuteUpdateAsync(s => s.SetProperty(l => l.ConfigurationState, "Invalid"));
        var repository = new LibraryExperienceRepository(context);
        (await repository.GetAttachmentsAsync(seed.First, default)).Single().VisibleCount.ShouldBe(0);
        (await repository.GetPreviewAsync(seed.First, null, 0, int.MinValue, default)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migration_AttachesExistingCollectionsButLeavesNewLibrariesExplicit()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var migrator = context.GetService<IMigrator>();
        const string attachmentMigration = "20260907004732_LibraryCollectionAttachments";
        var previous = context.Database.GetMigrations()
            .TakeWhile(migration => migration != attachmentMigration).Last();
        await migrator.MigrateAsync(previous);
        // Seed the historical contract explicitly: the current EF model includes columns
        // introduced after this migration, so it cannot write to the downgraded schema.
        const int libraryId = 1001;
        const int collectionId = 1001;
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var createdBy = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Libraries"
                ("Id", "Name", "ConfigurationJson", "NeedsMaterialization", "ItemCount", "CreatedAt", "CreatedByUserId")
            VALUES ({libraryId}, {"Existing audience"}, {"{}"}, {true}, {0}, {createdAt}, {createdBy})
            """);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Collections" ("Id", "Name", "SortOrder", "CreatedAt", "CreatedByUserId")
            VALUES ({collectionId}, {"Existing picks"}, {7}, {createdAt}, {createdBy})
            """);
        await migrator.MigrateAsync();
        var attachment = await context.LibraryCollections.SingleAsync();
        attachment.LibraryId.ShouldBe(libraryId);
        attachment.CollectionId.ShouldBe(collectionId);
        attachment.SortOrder.ShouldBe(7);
        attachment.IsFeatured.ShouldBeTrue();
        context.Libraries.Add(LibraryEntity.FromDomain(Library.CreateNew("New audience", new LibraryConfiguration())));
        await context.SaveChangesAsync();
        (await context.LibraryCollections.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Migration_AtomicBackendMutations_PreservesExistingDataAndAddsRevisions()
    {
        var seed = await SeedAsync();
        await SetAsync(seed.First, [(seed.Collection, true)]);
        await using var context = CreateContext();
        var librariesBefore = await context.Libraries.OrderBy(library => library.Id).ToListAsync();
        var titlesBefore = await context.Titles.OrderBy(title => title.Id).ToListAsync();
        var migrator = context.GetService<IMigrator>();
        const string mutationMigration = "20260909223113_AtomicBackendMutations";
        string previous = context.Database.GetMigrations()
            .TakeWhile(migration => migration != mutationMigration).Last();
        await migrator.MigrateAsync(previous);

        await migrator.MigrateAsync();

        var libraries = await context.Libraries.OrderBy(library => library.Id).ToListAsync();
        libraries.Select(library => (library.Id, library.Name, library.ConfigurationJson, library.UpdatedAt))
            .ShouldBe(librariesBefore.Select(library => (library.Id, library.Name, library.ConfigurationJson, library.UpdatedAt)));
        libraries.ShouldAllBe(library => library.MaterializationRevision != Guid.Empty);
        libraries.Select(library => library.MaterializationRevision).Distinct().Count().ShouldBe(libraries.Count);
        var titles = await context.Titles.OrderBy(title => title.Id).ToListAsync();
        titles.Select(title => (title.Id, title.Name, title.PlatformId, title.UpdatedAt))
            .ShouldBe(titlesBefore.Select(title => (title.Id, title.Name, title.PlatformId, title.UpdatedAt)));
        // Existing title snapshots begin at the migration sentinel and rotate on their first write.
        titles.ShouldAllBe(title => title.Revision == Guid.Empty);
        (await context.CollectionItems.CountAsync()).ShouldBe(2);
        (await context.LibraryCollections.SingleAsync()).CollectionId.ShouldBe(seed.Collection);
        (await context.MaterializedLibraryTitles.CountAsync()).ShouldBe(3);
        (await context.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_KeysetPages_PreserveCollectionOrderWithoutDuplicates()
    {
        var seed = await SeedAsync();
        await SetAsync(seed.Second, [(seed.Collection, true)]);
        await using var context = CreateContext();
        int firstTitleId = await context.Titles.Where(t => t.Name == "Second Game").Select(t => t.Id).SingleAsync();
        var page = await new LibraryExperienceRepository(context).GetPreviewAsync(seed.Second, seed.Collection, firstTitleId, 0, default);
        page.Items.Select(t => t.Name).ShouldBe(["First Game"]);
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task DraftEvaluation_ComparesPoliciesExplainsRemovalAndNeverWrites()
    {
        var seed = await SeedAsync();
        await SetAsync(seed.First, [(seed.Collection, true)]);
        await using var context = CreateContext();
        var titles = await context.Titles.OrderBy(t => t.Id).ToListAsync();
        var provider = Substitute.For<ILibraryCandidateReader>();
        provider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(titles.Select((t, index) => new TitleCandidates(t.Id, t.PlatformId, null,
                [new ContentRating { Board = RatingBoard.Esrb, Code = index == 0 ? "E" : "T",
                    MinimumAge = index == 0 ? 0 : 13, Designation = RatingDesignation.Rated, SourceId = "user" }],
                [new GameCandidate(t.Id, t.Id, 1, null, true, true, [1], [], [])])).ToList());
        var catalog = Substitute.For<ICatalogProjectionService>();
        catalog.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        var evaluator = new LibraryDraftEvaluator(context, new LibraryRepository(context), provider, catalog);
        var configuration = new LibraryConfiguration { ContentRatingPolicy = new() { MaxMinimumAge = 10 } };
        var before = await context.Libraries.AsNoTracking().SingleAsync(l => l.Id == seed.First);
        var result = await evaluator.EvaluateAsync(seed.First, configuration, "removed", null, 0, default);
        result.IsError.ShouldBeFalse();
        result.Value.SavedCount.ShouldBe(2);
        result.Value.MatchingCount.ShouldBe(1);
        result.Value.RemovedCount.ShouldBe(1);
        result.Value.AddedCount.ShouldBe(0);
        var removed = result.Value.Items.Single();
        removed.Reason.ShouldBe("ContentRating");
        removed.RatingCategory.ShouldBe("T");
        removed.RatingAge.ShouldBe(13);
        removed.AttachedCollectionCount.ShouldBe(1);
        result.Value.RemovalReasons.Single().Count.ShouldBe(1);
        var after = await context.Libraries.AsNoTracking().SingleAsync(l => l.Id == seed.First);
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        after.ConfigurationJson.ShouldBe(before.ConfigurationJson);
        after.NeedsMaterialization.ShouldBe(before.NeedsMaterialization);
        context.ChangeTracker.HasChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task DraftEvaluation_DirtyCatalog_DoesNotReturnPartialCounts()
    {
        var seed = await SeedAsync();
        await using var context = CreateContext();
        var provider = Substitute.For<ILibraryCandidateReader>();
        var catalog = Substitute.For<ICatalogProjectionService>();
        catalog.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>()).Returns(new[] { 1 });
        var evaluator = new LibraryDraftEvaluator(context, new LibraryRepository(context), provider, catalog);
        var result = await evaluator.EvaluateAsync(seed.First, new(), "matching", null, 0, default);
        result.FirstError.Code.ShouldBe("Libraries.CatalogUpdating");
        await provider.DidNotReceive().GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DraftEvaluation_InvalidPolicy_IsRejectedBeforeReadingCandidates()
    {
        await using var context = CreateContext();
        var provider = Substitute.For<ILibraryCandidateReader>();
        var evaluator = new LibraryDraftEvaluator(context, new LibraryRepository(context), provider,
            Substitute.For<ICatalogProjectionService>());
        var result = await evaluator.EvaluateAsync(1,
            new() { ContentRatingPolicy = new() { MaxMinimumAge = -1 } }, "matching", null, 0, default);
        result.FirstError.Code.ShouldBe("Libraries.InvalidConfiguration");
        await provider.DidNotReceive().GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>());
    }

    private async Task SetAsync(int libraryId, IReadOnlyList<(int, bool)> items)
    {
        await using var context = CreateContext();
        var handler = new SetLibraryAttachmentsCommandHandler(new LibraryExperienceRepository(context), new EfUnitOfWork(context));
        var result = await handler.HandleAsync(new(libraryId, items));
        result.IsError.ShouldBeFalse();
    }

    private async Task<(int First, int Second, int Collection, Guid User)> SeedAsync()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var first = LibraryEntity.FromDomain(Library.CreateNew("Kids", new LibraryConfiguration()));
        var second = LibraryEntity.FromDomain(Library.CreateNew("Everyone", new LibraryConfiguration()));
        first.NeedsMaterialization = false;
        second.NeedsMaterialization = false;
        context.Libraries.AddRange(first, second);
        var user = Guid.NewGuid();
        var platform = new PlatformEntity { Name = "NES", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "NES", BaseCompactLabel = "NES", CanonicalKey = "NES", ShortName = "NES", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = user };
        context.Platforms.Add(platform);
        var collection = new CollectionEntity { Name = "Shared Picks", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = user };
        context.Collections.Add(collection);
        await context.SaveChangesAsync();
        context.Users.Add(new RomdUser { Id = user, UserName = "family", LibraryId = first.Id, CreatedAt = DateTimeOffset.UtcNow });
        var titles = new[] { "First Game", "Second Game" }.Select(name => new TitleEntity {
            Name = name, EnrichmentStatus = "Pending", NormalizedName = name.ToLowerInvariant(), PlatformId = platform.Id, CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = user
        }).ToArray();
        context.Titles.AddRange(titles);
        await context.SaveChangesAsync();
        for (int i = 0; i < titles.Length; i++)
        {
            context.CollectionItems.Add(new CollectionItemEntity { CollectionId = collection.Id, TitleId = titles[i].Id, SortOrder = 1 - i, AddedAt = DateTimeOffset.UtcNow });
            foreach (var library in i == 0 ? new[] { first, second } : new[] { second })
                context.MaterializedLibraryTitles.Add(new MaterializedLibraryTitleEntity {
                    LibraryId = library.Id, TitleId = titles[i].Id, PlatformId = platform.Id,
                    IsVisible = true, IsOwned = true, IsPlayable = true, EligibleReleaseCount = 1, PlayableReleaseCount = 1,
                    ExposedReleaseCount = 1, Availability = "Complete"
                });
        }
        await context.SaveChangesAsync();
        return (first.Id, second.Id, collection.Id, user);
    }

    public ValueTask DisposeAsync() => database.DisposeAsync();
}
