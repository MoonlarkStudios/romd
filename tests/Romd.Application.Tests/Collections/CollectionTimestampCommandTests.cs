using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Collections;
using Romd.Admin.Application.Collections.Commands.AddTitle;
using Romd.Admin.Application.Collections.Commands.CreateCollection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Domain.Collections;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Collections;

public sealed class CollectionTimestampCommandTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public async Task CreateCollection_InjectedClock_PassesOneExactTimestampThroughStagingAndResponse()
    {
        var repository = Substitute.For<ICollectionRepository>();
        var receipt = Substitute.For<IStagedCollectionCreation>();
        Collection? staged = null;
        repository.AddStagedAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            staged = call.Arg<Collection>();
            return receipt;
        });
        receipt.ReadAfterFlush().Returns(_ => Collection.Rehydrate(42, staged!.Name, staged.Description,
            staged.CoverMediaId, staged.PlatformId, staged.IsSystem, staged.SortOrder, staged.CreatedAt));
        var clock = new AdvancingClock(Now);
        var handler = new CreateCollectionCommandHandler(TestSystemCatalog.Create(), repository,
            Substitute.For<ICollectionCoverMediaReader>(), Work(), clock,
            NullLogger<CreateCollectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CreateCollectionCommand("Favorites", null, null, null));

        result.IsError.ShouldBeFalse();
        staged.ShouldNotBeNull().CreatedAt.EqualsExact(Now).ShouldBeTrue();
        result.Value.CreatedAt.EqualsExact(Now).ShouldBeTrue();
        clock.ReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task AddTitle_InjectedClock_PassesOneExactTimestampThroughAggregateAndResponse()
    {
        var repository = Substitute.For<ICollectionRepository>();
        var collection = Collection.Rehydrate(42, "Favorites", null, null, null, false, 0, Now.AddYears(-1));
        repository.LockForItemMutationStagedAsync(42, Arg.Any<CancellationToken>()).Returns(collection);
        CollectionItem? staged = null;
        repository.AddItemStagedAsync(Arg.Any<CollectionItem>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            staged = call.Arg<CollectionItem>();
            return Task.CompletedTask;
        });
        repository.GetItemDetailAsync(42, 7, Arg.Any<CancellationToken>()).Returns(_ =>
            new CollectionItemReadModel(7, "Game", 1, null, staged!.Note, staged.SortOrder, staged.AddedAt));
        var clock = new AdvancingClock(Now);

        var result = await AddHandler(repository, clock)
            .HandleAsync(new AddTitleToCollectionCommand(42, 7, "Play next"));

        result.IsError.ShouldBeFalse();
        staged.ShouldNotBeNull().AddedAt.EqualsExact(Now).ShouldBeTrue();
        result.Value.AddedAt.EqualsExact(Now).ShouldBeTrue();
        collection.CreatedAt.EqualsExact(Now.AddYears(-1)).ShouldBeTrue();
        clock.ReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task AddTitle_DuplicateTitle_PreservesOriginalTimestampAndDoesNotStageReplacement()
    {
        var repository = Substitute.For<ICollectionRepository>();
        var existing = CollectionItem.Rehydrate(9, 42, 7, 0, "Original", Now.AddDays(-1));
        var collection = Collection.Rehydrate(42, "Favorites", null, null, null, false, 0, Now.AddYears(-1), [existing]);
        repository.LockForItemMutationStagedAsync(42, Arg.Any<CancellationToken>()).Returns(collection);

        var result = await AddHandler(repository, new AdvancingClock(Now))
            .HandleAsync(new AddTitleToCollectionCommand(42, 7, "Replacement note"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(CollectionErrors.DuplicateTitle().Code);
        collection.Items.ShouldHaveSingleItem().ShouldBeSameAs(existing);
        existing.AddedAt.EqualsExact(Now.AddDays(-1)).ShouldBeTrue();
        existing.Note.ShouldBe("Original");
        await repository.DidNotReceiveWithAnyArgs().AddItemStagedAsync(default!, default);
    }

    private static IUnitOfWork Work()
    {
        var work = Substitute.For<IUnitOfWork>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        return work;
    }

    private static AddTitleToCollectionCommandHandler AddHandler(ICollectionRepository repository, TimeProvider clock)
    {
        var titles = Substitute.For<ITitleReader>();
        titles.GetSummaryAsync(7, Arg.Any<CancellationToken>()).Returns(new TitleSummary(
            7, 1, "Game", EnrichmentStatus.None, null, null, null, null, null, null, null, Now.AddYears(-1), null, false));
        return new AddTitleToCollectionCommandHandler(TestSystemCatalog.Create(), repository, titles, Work(), clock,
            NullLogger<AddTitleToCollectionCommandHandler>.Instance);
    }

    private sealed class AdvancingClock(DateTimeOffset firstInstant) : TimeProvider
    {
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow() => firstInstant.AddDays(ReadCount++);
    }
}
