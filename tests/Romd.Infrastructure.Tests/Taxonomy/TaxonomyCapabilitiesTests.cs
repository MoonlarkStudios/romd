using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.ReferenceData;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public sealed class TaxonomyCapabilitiesTests
{
    [Fact]
    public async Task Lists_DistinguishRegisteredIdentitiesAndAliasAuthority()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var region = await db.Regions.SingleAsync(x => x.CanonicalKey == "asia");
        var language = await db.GameLanguages.SingleAsync(x => x.CanonicalKey == "en");
        db.Regions.Add(new RegionEntity { Name = "Unregistered region", IsAutoCreated = true, CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Unregistered region") });
        db.GameLanguages.Add(new GameLanguageEntity { Name = "Unregistered language", Code = "unknown", IsAutoCreated = true, CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Unregistered language") });
        db.RegionAliases.Add(new RegionAliasEntity { RegionId = region.Id, NormalizedAlias = "my-region", Ownership = ReferenceOwnership.Installation });
        db.RegionAliases.Add(new RegionAliasEntity { RegionId = region.Id, NormalizedAlias = "old-region", Ownership = null });
        db.GameLanguageAliases.Add(new GameLanguageAliasEntity { GameLanguageId = language.Id, NormalizedAlias = "my-language", Ownership = ReferenceOwnership.Installation });
        db.GameLanguageAliases.Add(new GameLanguageAliasEntity { GameLanguageId = language.Id, NormalizedAlias = "old-language", Ownership = null });
        await db.SaveChangesAsync();
        var regions = (await new RegionRepository(db).ListWithAliasesAsync(Page("regions"))).Items;
        var languages = (await new LanguageRepository(db).ListWithAliasesAsync(Page("languages"))).Items;
        regions.Single(x => x.Key == "asia").CanMerge.ShouldBeFalse();
        languages.Single(x => x.Key == "en").CanMerge.ShouldBeFalse();
        regions.Single(x => x.Key == "dat-unregistered-region").CanMerge.ShouldBeTrue();
        languages.Single(x => x.Code == "unknown").CanMerge.ShouldBeTrue();
        foreach (var aliases in new[] { regions.Single(x => x.Key == "asia").Aliases.Items, languages.Single(x => x.Key == "en").Aliases.Items })
        {
            aliases.ShouldContain(x => x.Ownership == "Romd");
            aliases.ShouldContain(x => x.Ownership == "Installation");
            aliases.ShouldContain(x => x.Ownership == "Unresolved");
        }
    }

    [Fact]
    public async Task List_PagesEveryRowInSortOrderNameKeyOrder_WithABoundCursor()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        db.Regions.AddRange(
            new RegionEntity { Name = "Zeta", SortOrder = 999, IsAutoCreated = true, CanonicalKey = "dat-zeta" },
            new RegionEntity { Name = "Alpha", SortOrder = 999, IsAutoCreated = true, CanonicalKey = "dat-alpha-2" },
            new RegionEntity { Name = "Alpha", SortOrder = 999, IsAutoCreated = true, CanonicalKey = "dat-alpha" });
        await db.SaveChangesAsync();
        var repository = new RegionRepository(db);
        // Keyset continuation must agree with the database's own ordering, collation included.
        var expected = await db.Regions.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ThenBy(x => x.CanonicalKey)
            .Select(x => x.CanonicalKey).ToArrayAsync();

        var keys = new List<string>();
        string? cursor = null;
        do
        {
            var page = await repository.ListWithAliasesAsync(Page("regions", cursor, 2));
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            keys.AddRange(page.Items.Select(x => x.Key));
            page.HasNextPage.ShouldBe(page.NextCursor is not null);
            cursor = page.NextCursor;
        } while (cursor is not null);

        keys.ShouldBe(expected);
        keys.TakeLast(3).ShouldBe(["dat-alpha", "dat-alpha-2", "dat-zeta"]);
        var first = await repository.ListWithAliasesAsync(Page("regions", null, 2));
        TaxonomyPageRequest.Create("languages", first.NextCursor, 2).FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
        TaxonomyPageRequest.Create("regions", first.NextCursor + "A", 2).IsError.ShouldBeTrue();
        TaxonomyPageRequest.Create("regions", "not a cursor", 2).FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
    }

    [Fact]
    public async Task Get_RevisionCoversAliases()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var repository = new RegionRepository(db);
        var before = (await repository.GetWithAliasesAsync("asia"))!;

        (await new TaxonomyService<Region>(repository, new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance).AddAliasAsync("asia", "my-asia", default)).IsError.ShouldBeFalse();

        var after = (await repository.GetWithAliasesAsync("asia"))!;
        after.Revision.ShouldNotBe(before.Revision);
        after.Ownership.ShouldBe("Romd");
        (await repository.GetWithAliasesAsync("dat-unknown")).ShouldBeNull();
    }

    [Fact]
    public async Task Rows_EmbedTheirFirstAliasPage_AndTheAliasCollectionContinuesItInIdOrder()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var many = await AddRegionAsync(db, "Atlantis", TaxonomyAliasPageRequest.EmbeddedLimit + 15);
        var exact = await AddRegionAsync(db, "Lemuria", TaxonomyAliasPageRequest.EmbeddedLimit);
        var ids = await db.RegionAliases.Where(alias => alias.RegionId == many.Id).OrderBy(alias => alias.Id)
            .Select(alias => alias.Id).ToListAsync();
        var repository = new RegionRepository(db);

        var entry = (await repository.GetWithAliasesAsync(many.CanonicalKey))!;
        var listed = (await repository.ListWithAliasesAsync(Page("regions"))).Items;

        entry.Aliases.Items.Select(alias => alias.Id).ShouldBe(ids.Take(TaxonomyAliasPageRequest.EmbeddedLimit));
        entry.Aliases.HasNextPage.ShouldBeTrue();
        listed.Single(x => x.Key == many.CanonicalKey).Aliases.Items.Select(alias => alias.Id)
            .ShouldBe(entry.Aliases.Items.Select(alias => alias.Id));
        var full = listed.Single(x => x.Key == exact.CanonicalKey).Aliases;
        (full.Items.Count, full.HasNextPage).ShouldBe((TaxonomyAliasPageRequest.EmbeddedLimit, false));

        var traversed = entry.Aliases.Items.Select(alias => alias.Id).ToList();
        string? cursor = TaxonomyAliasPageRequest.CursorFor("regions", many.CanonicalKey, traversed[^1]);
        while (cursor is not null)
        {
            var page = await repository.ListAliasesAsync(many.Id, AliasPage(many.CanonicalKey, cursor, 7));
            page.Items.Count.ShouldBeLessThanOrEqualTo(7);
            page.HasNextPage.ShouldBe(page.NextCursor is not null);
            traversed.AddRange(page.Items.Select(alias => alias.Id));
            cursor = page.NextCursor;
        }

        traversed.ShouldBe(ids);
    }

    [Fact]
    public async Task AliasCollection_AliasAddedDuringATraversal_AppearsOnceAfterTheCursor()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var row = await AddRegionAsync(db, "Atlantis", 12);
        var repository = new RegionRepository(db);

        var first = await repository.ListAliasesAsync(row.Id, AliasPage(row.CanonicalKey, null, 10));
        var added = (await new TaxonomyService<Region>(repository, new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance)
            .AddAliasAsync(row.CanonicalKey, "atlantis added", default)).Value;
        var rest = await repository.ListAliasesAsync(row.Id, AliasPage(row.CanonicalKey, first.NextCursor, 10));

        var traversed = first.Items.Concat(rest.Items).Select(alias => alias.Id).ToList();
        traversed.Count.ShouldBe(13);
        traversed.Distinct().Count().ShouldBe(13);
        traversed[^1].ShouldBe(added.Id);
        rest.HasNextPage.ShouldBeFalse();
    }

    [Fact]
    public async Task List_QueryMatchesNameKeyOrAnyAlias_AndItsCursorStaysWithinTheQuery()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var atlantis = await AddRegionAsync(db, "Atlantis", TaxonomyAliasPageRequest.EmbeddedLimit + 5);
        // The match lies beyond the first page a row embeds, so only a server-side search finds it.
        db.RegionAliases.Add(new RegionAliasEntity { RegionId = atlantis.Id, NormalizedAlias = "sunken needle" });
        await AddRegionAsync(db, "Needle Point", 0);
        await AddRegionAsync(db, "Lemuria", 1);
        await db.SaveChangesAsync();
        var repository = new RegionRepository(db);

        var first = await repository.ListWithAliasesAsync(Page("regions", limit: 1, query: "Néedle"));
        var second = await repository.ListWithAliasesAsync(Page("regions", first.NextCursor, 1, "needle"));

        first.Items.Select(x => x.Key).ShouldBe([atlantis.CanonicalKey]);
        second.Items.Select(x => x.Key).ShouldBe(["dat-needle-point"]);
        second.HasNextPage.ShouldBeFalse();
        (await repository.ListWithAliasesAsync(Page("regions", query: "LEMURIA"))).Items.Select(x => x.Key).ShouldBe(["dat-lemuria"]);
        (await repository.ListWithAliasesAsync(Page("regions", query: "dat-lemuria"))).Items.Select(x => x.Key).ShouldBe(["dat-lemuria"]);
        (await repository.ListWithAliasesAsync(Page("regions", query: "mu"))).Items.Select(x => x.Key).ShouldBe(["dat-lemuria"]);
        (await repository.ListWithAliasesAsync(Page("regions", query: "nowhere"))).Items.ShouldBeEmpty();
        TaxonomyPageRequest.Create("regions", first.NextCursor, 1, "point").FirstError.Code.ShouldBe("Taxonomy.InvalidCursor");
    }

    [Fact]
    public async Task List_LanguageQueryAlsoMatchesTheCode()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.GameLanguages.Add(new GameLanguageEntity
        {
            Name = "Klingon",
            Code = "tlh",
            SortOrder = 999,
            IsAutoCreated = true,
            CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Klingon")
        });
        db.GameLanguages.Add(new GameLanguageEntity
        {
            Name = "Vulcan",
            Code = "vul",
            SortOrder = 999,
            IsAutoCreated = true,
            CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Vulcan")
        });
        await db.SaveChangesAsync();

        var page = await new LanguageRepository(db).ListWithAliasesAsync(Page("languages", query: "TLH"));

        page.Items.Select(x => x.Key).ShouldBe(["dat-klingon"]);
    }

    [Fact]
    public async Task Get_RevisionCoversTheEmbeddedAliasPage_NotAliasesBeyondIt()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var row = await AddRegionAsync(db, "Atlantis", TaxonomyAliasPageRequest.EmbeddedLimit);
        var repository = new RegionRepository(db);
        async Task<string> RevisionAsync() => (await repository.GetWithAliasesAsync(row.CanonicalKey))!.Revision;

        string full = await RevisionAsync();
        var service = new TaxonomyService<Region>(repository, new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);
        (await service.AddAliasAsync(row.CanonicalKey, "atlantis overflow", default)).IsError.ShouldBeFalse();
        string overflowing = await RevisionAsync();
        (await service.AddAliasAsync(row.CanonicalKey, "atlantis overflow 2", default)).IsError.ShouldBeFalse();
        string beyond = await RevisionAsync();
        int firstId = await db.RegionAliases.Where(alias => alias.RegionId == row.Id).MinAsync(alias => alias.Id);
        await db.RegionAliases.Where(alias => alias.Id == firstId).ExecuteDeleteAsync();
        string shifted = await RevisionAsync();

        overflowing.ShouldNotBe(full);
        beyond.ShouldBe(overflowing);
        shifted.ShouldNotBe(beyond);
    }

    // An unregistered row as DAT ingest leaves it, with aliases named after it in insertion order.
    private static async Task<RegionEntity> AddRegionAsync(RomdDbContext db, string name, int aliases)
    {
        var row = new RegionEntity { Name = name, SortOrder = 999, IsAutoCreated = true, CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor(name) };
        db.Regions.Add(row);
        await db.SaveChangesAsync();
        foreach (int index in Enumerable.Range(1, aliases))
        {
            db.RegionAliases.Add(new RegionAliasEntity { RegionId = row.Id, NormalizedAlias = $"{name.ToLowerInvariant()} {index:D2}" });
            await db.SaveChangesAsync();
        }
        return row;
    }

    private static TaxonomyPageRequest Page(string collection, string? cursor = null, int limit = TaxonomyPageRequest.MaxLimit,
        string? query = null) =>
        TaxonomyPageRequest.Create(collection, cursor, limit, query).Value;

    private static TaxonomyAliasPageRequest AliasPage(string key, string? cursor, int limit) =>
        TaxonomyAliasPageRequest.Create("regions", key, cursor, limit).Value;
}
