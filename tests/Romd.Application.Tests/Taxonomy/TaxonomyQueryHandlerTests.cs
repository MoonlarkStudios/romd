using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.Queries.GetLanguage;
using Romd.Admin.Application.Taxonomy.Queries.GetRegion;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguageAliases;
using Romd.Admin.Application.Taxonomy.Queries.ListRegionAliases;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguages;
using Romd.Admin.Application.Taxonomy.Queries.ListRegions;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Pagination;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Taxonomy;

/// <summary>The region and language management reads take the row and its aliases from one snapshot.</summary>
public sealed class TaxonomyQueryHandlerTests
{
    private readonly IReadSnapshotTransactionFactory snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
    private readonly IReadSnapshotTransaction snapshot = Substitute.For<IReadSnapshotTransaction>();

    public TaxonomyQueryHandlerTests() => snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(snapshot);

    [Fact]
    public async Task GetRegion_HandleAsync_ReadsTheEntryInsideOneCompletedSnapshot()
    {
        var repository = Substitute.For<IRegionRepository>();
        var entry = Entry("dat-atlantis");
        repository.GetWithAliasesAsync("dat-atlantis", Arg.Any<CancellationToken>()).Returns(entry);

        var result = await new GetRegionQueryHandler(repository, snapshots).HandleAsync(new GetRegionQuery("dat-atlantis"));

        result.Value.ShouldBeSameAs(entry);
        Received.InOrder(() =>
        {
            _ = snapshots.BeginAsync(Arg.Any<CancellationToken>());
            _ = repository.GetWithAliasesAsync("dat-atlantis", Arg.Any<CancellationToken>());
            _ = snapshot.CompleteAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task GetLanguage_UnknownKey_CompletesTheSnapshotAndIsNotFound()
    {
        var repository = Substitute.For<ILanguageRepository>();
        repository.GetWithAliasesAsync("dat-klingon", Arg.Any<CancellationToken>()).Returns((TaxonomyEntry?)null);

        var result = await new GetLanguageQueryHandler(repository, snapshots).HandleAsync(new GetLanguageQuery("dat-klingon"));

        result.FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        Received.InOrder(() =>
        {
            _ = snapshots.BeginAsync(Arg.Any<CancellationToken>());
            _ = repository.GetWithAliasesAsync("dat-klingon", Arg.Any<CancellationToken>());
            _ = snapshot.CompleteAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ListRegions_HandleAsync_ReadsThePageInsideOneCompletedSnapshot()
    {
        var repository = Substitute.For<IRegionRepository>();
        var page = new PagedList<TaxonomyEntry>([Entry("dat-atlantis")], null, false);
        repository.ListWithAliasesAsync(Arg.Any<TaxonomyPageRequest>(), Arg.Any<CancellationToken>()).Returns(page);

        var result = await new ListRegionsQueryHandler(repository, snapshots).HandleAsync(new ListRegionsQuery());

        result.Value.ShouldBeSameAs(page);
        Received.InOrder(() =>
        {
            _ = snapshots.BeginAsync(Arg.Any<CancellationToken>());
            _ = repository.ListWithAliasesAsync(Arg.Any<TaxonomyPageRequest>(), Arg.Any<CancellationToken>());
            _ = snapshot.CompleteAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ListLanguages_HandleAsync_ReadsThePageInsideOneCompletedSnapshot()
    {
        var repository = Substitute.For<ILanguageRepository>();
        var page = new PagedList<TaxonomyEntry>([Entry("dat-klingon")], null, false);
        repository.ListWithAliasesAsync(Arg.Any<TaxonomyPageRequest>(), Arg.Any<CancellationToken>()).Returns(page);

        var result = await new ListLanguagesQueryHandler(repository, snapshots).HandleAsync(new ListLanguagesQuery());

        result.Value.ShouldBeSameAs(page);
        Received.InOrder(() =>
        {
            _ = snapshots.BeginAsync(Arg.Any<CancellationToken>());
            _ = repository.ListWithAliasesAsync(Arg.Any<TaxonomyPageRequest>(), Arg.Any<CancellationToken>());
            _ = snapshot.CompleteAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ListRegions_InvalidCursor_IsRefusedBeforeASnapshotOpens()
    {
        var repository = Substitute.For<IRegionRepository>();

        var result = await new ListRegionsQueryHandler(repository, snapshots).HandleAsync(new ListRegionsQuery("not-a-cursor"));

        result.FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        await snapshots.DidNotReceive().BeginAsync(Arg.Any<CancellationToken>());
        await repository.DidNotReceive().ListWithAliasesAsync(Arg.Any<TaxonomyPageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRegionAliases_HandleAsync_ResolvesTheRowAndReadsItsPageInsideOneCompletedSnapshot()
    {
        var repository = Substitute.For<IRegionRepository>();
        var page = new PagedList<TaxonomyAlias>([new TaxonomyAlias(7, "atlantis", "Installation")], null, false);
        repository.GetIdByKeyAsync("dat-atlantis", Arg.Any<CancellationToken>()).Returns(3);
        repository.ListAliasesAsync(3, Arg.Is<TaxonomyAliasPageRequest>(request => request.Key == "dat-atlantis"),
            Arg.Any<CancellationToken>()).Returns(page);

        var result = await new ListRegionAliasesQueryHandler(repository, snapshots)
            .HandleAsync(new ListRegionAliasesQuery("dat-atlantis"));

        result.Value.ShouldBeSameAs(page);
        Received.InOrder(() =>
        {
            _ = snapshots.BeginAsync(Arg.Any<CancellationToken>());
            _ = repository.GetIdByKeyAsync("dat-atlantis", Arg.Any<CancellationToken>());
            _ = repository.ListAliasesAsync(3, Arg.Any<TaxonomyAliasPageRequest>(), Arg.Any<CancellationToken>());
            _ = snapshot.CompleteAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ListLanguageAliases_UnknownKey_CompletesTheSnapshotAndIsNotFound()
    {
        var repository = Substitute.For<ILanguageRepository>();
        repository.GetIdByKeyAsync("dat-klingon", Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await new ListLanguageAliasesQueryHandler(repository, snapshots)
            .HandleAsync(new ListLanguageAliasesQuery("dat-klingon"));

        result.FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        await repository.DidNotReceive().ListAliasesAsync(Arg.Any<int>(), Arg.Any<TaxonomyAliasPageRequest>(), Arg.Any<CancellationToken>());
        await snapshot.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRegionAliases_CursorFromAnotherRow_IsRefusedBeforeASnapshotOpens()
    {
        var repository = Substitute.For<IRegionRepository>();
        string cursor = TaxonomyAliasPageRequest.CursorFor(ListRegionsQueryHandler.Collection, "dat-lemuria", 9);

        var result = await new ListRegionAliasesQueryHandler(repository, snapshots)
            .HandleAsync(new ListRegionAliasesQuery("dat-atlantis", cursor));

        result.FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        await snapshots.DidNotReceive().BeginAsync(Arg.Any<CancellationToken>());
    }

    private static TaxonomyEntry Entry(string key) =>
        new(key, null, null, key, null, 999, false, true, null, new TaxonomyAliasPage([], false), "revision");
}
