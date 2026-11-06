using Romd.Admin.Application.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

/// <summary>DAT ingest mints a key once, when a token first creates a row, and every later import reuses the row.</summary>
public sealed class TaxonomyKeyMintingTests
{
    [Fact]
    public async Task Resolve_UnknownTokens_MintKeysFromTheNormalizedToken()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await using var ingest = await db.Database.BeginTransactionAsync();

        var ids = await Regions(db).ResolveAsync("USA, Hong Kong (HK), 日本");

        var keys = await db.Regions.Where(row => ids.Contains(row.Id)).OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync();
        keys.ShouldBe(["dat-usa", "dat-hong-kong-hk", "dat-cf2abf0c5b"]);
        (await db.Regions.CountAsync(row => row.Ownership != null)).ShouldBe(0);
        var languageId = (await Languages(db).ResolveAsync("Klingon")).ShouldHaveSingleItem();
        var language = await db.GameLanguages.SingleAsync(row => row.Id == languageId);
        (language.CanonicalKey, language.Code).ShouldBe(("dat-klingon", "klingon"));
    }

    [Fact]
    public async Task Resolve_LanguageTokenWithAnAlias_ResolvesToTheAliasedRowAndMintsNothing()
    {
        using var database = PostgreSqlTestDatabase.Create();
        int english;
        await using (var seed = database.CreateContext())
        {
            var row = new GameLanguageEntity { Name = "English", Code = "en", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("English") };
            seed.GameLanguages.Add(row);
            await seed.SaveChangesAsync();
            seed.GameLanguageAliases.Add(new GameLanguageAliasEntity { GameLanguageId = row.Id, NormalizedAlias = "en" });
            await seed.SaveChangesAsync();
            english = row.Id;
        }

        await using var db = database.CreateContext();
        await using var ingest = await db.Database.BeginTransactionAsync();
        (await Languages(db).ResolveAsync("EN")).ShouldHaveSingleItem().ShouldBe(english);
        await ingest.CommitAsync();

        (await db.GameLanguages.Select(row => row.CanonicalKey).ToListAsync()).ShouldBe(["dat-english"]);
    }

    [Fact]
    public async Task Resolve_ReImport_ReusesTheRowAndItsKey()
    {
        using var database = PostgreSqlTestDatabase.Create();
        int first;
        await using (var db = database.CreateContext())
        {
            await using var ingest = await db.Database.BeginTransactionAsync();
            first = (await Regions(db).ResolveAsync("USA")).ShouldHaveSingleItem();
            await ingest.CommitAsync();
        }

        await using (var db = database.CreateContext())
        {
            // A later import, in another transaction, reads the token from what the first committed.
            (await Regions(db).ResolveAsync("usa,  USA ")).ShouldHaveSingleItem().ShouldBe(first);
            (await db.Regions.SingleAsync()).CanonicalKey.ShouldBe("dat-usa");
        }
    }

    [Fact]
    public async Task Resolve_TokenWhoseRowLostItsAlias_MintsTheNextFreeCandidate()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await using var ingest = await db.Database.BeginTransactionAsync();
        var first = (await Regions(db).ResolveAsync("USA")).ShouldHaveSingleItem();
        await db.RegionAliases.Where(alias => alias.RegionId == first).ExecuteDeleteAsync();

        var second = (await Regions(db).ResolveAsync("USA")).ShouldHaveSingleItem();
        await db.RegionAliases.Where(alias => alias.RegionId == second).ExecuteDeleteAsync();
        var third = (await Regions(db).ResolveAsync("USA")).ShouldHaveSingleItem();

        var keys = await db.Regions.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync();
        new[] { first, second, third }.Distinct().Count().ShouldBe(3);
        keys.ShouldBe(["dat-usa", "dat-usa-5fc90ab335", "dat-usa-5fc90ab335-2"]);
    }

    [Fact]
    public async Task Resolve_UnknownTokenWithoutATransaction_RefusesToMint()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();

        // The minting lock lasts until the caller's transaction ends; without one, nothing would hold it.
        await Should.ThrowAsync<InvalidOperationException>(() => Regions(db).ResolveAsync("Atlantis"));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            new RegionRepository(db).AddStagedAsync(Region.CreateNew("Atlantis", sortOrder: 999, isAutoCreated: true), "atlantis"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new RegionRepository(db).ExecuteMintSavepointAsync(_ => Task.FromResult<int?>(null)));
        (await db.Regions.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Resolve_ConcurrentIngestsWhoseTokensShareASlug_MintDistinctKeys()
    {
        // Two ingests, in one process or two, ingest "USA" and "USA!", which both prefer dat-usa.
        using var database = PostgreSqlTestDatabase.Create();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await using var firstIngest = await first.Database.BeginTransactionAsync();
        await using var secondIngest = await second.Database.BeginTransactionAsync();

        (await Regions(first).ResolveAsync("USA")).ShouldHaveSingleItem();
        var pending = Regions(second).ResolveAsync("USA!");
        // The second ingest waits for the first to finish minting in its transaction.
        (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBe(pending);
        await firstIngest.CommitAsync();
        (await pending.WaitAsync(TimeSpan.FromSeconds(30))).ShouldHaveSingleItem();
        await secondIngest.CommitAsync();

        await using var check = database.CreateContext();
        (await check.Regions.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync())
            .ShouldBe(["dat-usa", UnregisteredTaxonomyKey.HashedStem("usa!")]);
    }

    [Fact]
    public async Task Resolve_ConcurrentIngestsMintingBothKindsInOppositeOrders_DoNotDeadlock()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await using var firstIngest = await first.Database.BeginTransactionAsync();
        await using var secondIngest = await second.Database.BeginTransactionAsync();

        (await Regions(first).ResolveAsync("Atlantis")).ShouldHaveSingleItem();
        var secondLanguage = Languages(second).ResolveAsync("Klingon");
        // One lock covers both kinds, so the first ingest still mints its language while the second waits. With a
        // lock per kind, each ingest would hold one kind's lock and wait for the other's until commit: a deadlock.
        (await Languages(first).ResolveAsync("Elvish").WaitAsync(TimeSpan.FromSeconds(30))).ShouldHaveSingleItem();
        await firstIngest.CommitAsync();
        (await secondLanguage.WaitAsync(TimeSpan.FromSeconds(30))).ShouldHaveSingleItem();
        (await Regions(second).ResolveAsync("Lemuria")).ShouldHaveSingleItem();
        await secondIngest.CommitAsync();

        await using var check = database.CreateContext();
        (await check.Regions.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync()).ShouldBe(["dat-atlantis", "dat-lemuria"]);
        (await check.GameLanguages.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync()).ShouldBe(["dat-elvish", "dat-klingon"]);
    }

    [Fact]
    public async Task Resolve_AnIngestWaitingForTheMintingLock_HoldsNothingItsHolderNeeds()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await using var firstIngest = await first.Database.BeginTransactionAsync();
        await using var secondIngest = await second.Database.BeginTransactionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        (await Regions(first).ResolveAsync("Atlantis", timeout.Token)).ShouldHaveSingleItem();
        var (pending, waited) = await database.StartWaitingForALockAsync(second,
            ct => Regions(second).ResolveAsync("Lemuria", ct), timeout.Token);
        waited.ShouldBeTrue();
        // The minting lock's holder still mints its next new token while the other ingest waits.
        (await Regions(first).ResolveAsync("Mu", timeout.Token)).ShouldHaveSingleItem();
        await firstIngest.CommitAsync();
        (await pending).ShouldHaveSingleItem();
        await secondIngest.CommitAsync();

        await using var check = database.CreateContext();
        (await check.Regions.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync())
            .ShouldBe(["dat-atlantis", "dat-mu", "dat-lemuria"]);
    }

    private static TaxonomyResolver<Region> Regions(RomdDbContext db) =>
        new(new RegionRepository(db), new EfUnitOfWork(db), token => Region.CreateNew(token, sortOrder: 999, isAutoCreated: true));

    private static TaxonomyResolver<GameLanguage> Languages(RomdDbContext db) =>
        new(new LanguageRepository(db), new EfUnitOfWork(db),
            token => GameLanguage.CreateNew(token, token.ToLowerInvariant(), sortOrder: 999, isAutoCreated: true));
}
