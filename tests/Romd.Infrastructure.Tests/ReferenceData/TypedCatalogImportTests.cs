using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class TypedCatalogImportTests
{
    private static JsonNode Next()
    {
        var value = JsonNode.Parse(BundledReferenceData.Document.GetRawText())!;
        value["catalogVersion"] = value["catalogVersion"]!.GetValue<int>() + 1;
        return value;
    }
    private static Task Import(RomdDbContext db, JsonNode input) => SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance, document: JsonSerializer.SerializeToElement(input));

    [Fact]
    public async Task Import_ReconcilesOwnedAliasesAndMappings_PreservesLocalAdditions_AndIsIdempotent()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var psx = await db.Platforms.SingleAsync(x => x.CanonicalKey == "psx");
        var english = await db.GameLanguages.SingleAsync(x => x.CanonicalKey == "en");
        var region = await db.Regions.SingleAsync(x => x.CanonicalKey == "asia");
        db.PlatformAliases.Add(PlatformAliasEntity.FromDomain(PlatformAlias.CreateName(psx.Id, "my-playstation")));
        db.PlatformAliases.Add(PlatformAliasEntity.FromDomain(PlatformAlias.CreateProviderMapping(psx.Id, "local-provider", "123")));
        db.RegionAliases.Add(new() { RegionId = region.Id, NormalizedAlias = "local-asia" });
        db.GameLanguageAliases.Add(new() { GameLanguageId = english.Id, NormalizedAlias = "local-english" });
        await db.SaveChangesAsync();
        var next = Next();
        // Atari is visited first: an owned alias move must not depend on import order.
        next["systems"]!["psx"]!["aliases"] = new JsonArray("new-playstation");
        next["systems"]!["2600"]!["aliases"]!.AsArray().Add("ps1");
        next["systems"]!["psx"]!["providerMappings"]!["igdb"] = "999";
        next["regions"]!["asia"]!["aliases"] = new JsonArray("new-asia");
        next["languages"]!["en"]!["aliases"] = new JsonArray("new-english");
        await Import(db, next);
        (await db.PlatformAliases.SingleAsync(x => x.NormalizedValue == "ps1")).PlatformId.ShouldBe((await db.Platforms.SingleAsync(x => x.CanonicalKey == "2600")).Id);
        (await db.PlatformAliases.SingleAsync(x => x.PlatformId == psx.Id && x.Provider == "igdb")).Value.ShouldBe("999");
        (await db.PlatformAliases.SingleAsync(x => x.NormalizedValue == "my-playstation")).Ownership.ShouldBe(ReferenceOwnership.Installation);
        (await db.PlatformAliases.SingleAsync(x => x.Provider == "local-provider")).Ownership.ShouldBe(ReferenceOwnership.Installation);
        (await db.RegionAliases.AnyAsync(x => x.NormalizedAlias == "as")).ShouldBeFalse();
        (await db.GameLanguageAliases.AnyAsync(x => x.NormalizedAlias == "eng")).ShouldBeFalse();
        (await db.RegionAliases.SingleAsync(x => x.NormalizedAlias == "local-asia")).Ownership.ShouldBe(ReferenceOwnership.Installation);
        (await db.GameLanguageAliases.SingleAsync(x => x.NormalizedAlias == "local-english")).Ownership.ShouldBe(ReferenceOwnership.Installation);
        var ids = await db.PlatformAliases.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        var revision = await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync();
        await Import(db, next);
        (await db.PlatformAliases.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync()).ShouldBe(ids);
        (await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync()).ShouldBe(revision);
    }

    [Fact]
    public async Task Import_LocalAliasConflict_RollsBackFactsRelationshipsAndPublication()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var psx = await db.Platforms.SingleAsync(x => x.CanonicalKey == "psx");
        db.PlatformAliases.Add(PlatformAliasEntity.FromDomain(PlatformAlias.CreateName(psx.Id, "local-conflict")));
        await db.SaveChangesAsync();
        var oldName = (await db.Companies.SingleAsync(x => x.Key == "nintendo")).BaseName;
        var revision = await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync();
        var next = Next();
        next["companies"]!["nintendo"]!["name"] = "Must roll back";
        next["systems"]!["snes"]!["aliases"]!.AsArray().Add("local-conflict");
        await Should.ThrowAsync<InvalidDataException>(() => Import(db, next));
        (await db.Companies.SingleAsync(x => x.Key == "nintendo")).BaseName.ShouldBe(oldName);
        (await db.PlatformAliases.SingleAsync(x => x.NormalizedValue == "local-conflict")).PlatformId.ShouldBe(psx.Id);
        (await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync()).ShouldBe(revision);
    }

    [Fact]
    public async Task Import_UpdatesRomdOwnedFacts_QueriesAndSnapshotsAgree()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var regions = new RegionReferenceReader(db);
        var languages = new LanguageReferenceReader(db);
        var boards = new RatingBoardReferenceReader(db);
        var ratings = new RatingReferenceReader(db);
        var region = (await regions.GetAsync("asia", default))!;
        var language = (await languages.GetAsync("en", default))!;
        var board = (await boards.GetAsync("Esrb", default))!;
        var rating = (await ratings.GetAsync("Esrb:E", default))!;
        var next = Next();
        next["regions"]!["asia"]!["sortOrder"] = 80;
        next["languages"]!["en"]!["name"] = "New English";
        next["ratings"]!["boards"]!.AsArray().Single(x => x!["key"]!.GetValue<string>() == "Esrb")!["description"] = "New board description";
        var input = next["ratings"]!["ratings"]!.AsArray().Single(x => x!["board"]!.GetValue<string>() == "Esrb" && x["code"]!.GetValue<string>() == "E")!;
        input["description"] = "New rating description"; input["retired"] = true;
        await Import(db, next);
        (await db.Regions.SingleAsync(x => x.CanonicalKey == "asia")).BaseSortOrder.ShouldBe(80);
        (await db.Regions.SingleAsync(x => x.CanonicalKey == "asia")).SortOrder.ShouldBe(80);
        (await db.GameLanguages.SingleAsync(x => x.CanonicalKey == "en")).BaseName.ShouldBe("New English");
        (await db.GameLanguages.SingleAsync(x => x.CanonicalKey == "en")).Name.ShouldBe("New English");
        var current = (await ratings.GetAsync("Esrb:E", default))!;
        current.Resource.Icon.ShouldNotBeNull(); current.Resource.Description.ShouldBe("New rating description"); current.Resource.Retired.ShouldBeTrue();
        current.Resource.Designation.ShouldBe(rating.Resource.Designation);
        (await db.Ratings.SingleAsync(x => x.Key == "Esrb:E")).BaseAssetHash.ShouldNotBeNull();
        (await boards.GetAsync("Esrb", default))!.Resource.Description.ShouldBe("New board description");
        var snapshot = (await new ReferenceCatalogService(db, TestReferenceBlobs.Instance).GetCurrentAsync(default))!;
        snapshot.Regions.Single(x => x.Key == "asia").SortOrder.ShouldBe(80);
        snapshot.Languages.Single(x => x.Key == "en").Name.ShouldBe("New English");
        snapshot.Ratings.Single(x => x.Board == "Esrb" && x.Code == "E").Description.ShouldBe("New rating description");
    }

    [Fact]
    public async Task Migration_PreservesTypedFactsOverridesRelationshipsAndSnapshot()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var ratings = new RatingReferenceReader(db);
        var rating = (await ratings.GetAsync("Esrb:E", default))!;
        var before = JsonSerializer.Serialize(await ratings.GetAsync("Esrb:E", default));
        var regionId = (await db.Regions.SingleAsync(x => x.CanonicalKey == "asia")).Id;
        var revision = await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync();
        var owners = await db.ReferenceAssetOwners.CountAsync();
        await db.GetService<IMigrator>().MigrateAsync("20260917145649_TypedSystemsAndCompanies");
        await db.GetService<IMigrator>().MigrateAsync();
        db.ChangeTracker.Clear();
        JsonSerializer.Serialize(await ratings.GetAsync("Esrb:E", default)).ShouldBe(before);
        var row = await db.Regions.SingleAsync(x => x.CanonicalKey == "asia");
        row.Id.ShouldBe(regionId); row.Ownership.ShouldBe(ReferenceOwnership.Romd);
        (await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync()).ShouldBe(revision);
        (await db.ReferenceAssetOwners.CountAsync()).ShouldBe(owners);
    }

    [Fact]
    public async Task Registration_CanonicalKeyWithoutOwnership_IsRejected()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.Regions.Add(new RegionEntity { CanonicalKey = "asia", Name = "Unregistered facts" });
        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        ((PostgresException)error.InnerException!).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task MigratedMapping_UnresolvedProvenance_ReportsConflictUntilExplicitlyResolved()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        await db.GetService<IMigrator>().MigrateAsync("20260917145649_TypedSystemsAndCompanies");
        await db.GetService<IMigrator>().MigrateAsync();
        db.ChangeTracker.Clear();
        var id = (await db.Platforms.SingleAsync(x => x.CanonicalKey == "snes")).Id;
        var mapping = await db.PlatformAliases.SingleAsync(x => x.PlatformId == id && x.Provider == "igdb");
        mapping.Ownership.ShouldBeNull();
        var next = Next();
        next["systems"]!["snes"]!["providerMappings"]!["igdb"] = "999";
        var revision = await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync();
        (await Should.ThrowAsync<InvalidDataException>(() => Import(db, next))).Message.ShouldContain("Unresolved alias ownership");
        (await db.PlatformAliases.SingleAsync(x => x.Id == mapping.Id)).Value.ShouldBe(mapping.Value);
        (await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync()).ShouldBe(revision);
        // An explicit operator choice permits future updates; this is never inferred from equality.
        await db.PlatformAliases.Where(x => x.Id == mapping.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Ownership, ReferenceOwnership.Romd));
        await Import(db, next);
        (await db.PlatformAliases.SingleAsync(x => x.PlatformId == id && x.Provider == "igdb")).Value.ShouldBe("999");
    }

    [Fact]
    public async Task Publication_RebuildsDerivedArtworkOwnershipIndex_FromBaseFactsAndOverrides()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var expected = await db.ReferenceAssetOwners.OrderBy(x => x.Kind).ThenBy(x => x.Key).ThenBy(x => x.Slot)
            .Select(x => x.Kind + "/" + x.Key + "/" + x.Slot + "/" + x.Hash).ToArrayAsync();
        var hash = await db.ReferenceAssets.Select(x => x.Hash).FirstAsync();
        await db.ReferenceAssetOwners.ExecuteDeleteAsync();
        db.ReferenceAssetOwners.Add(new ReferenceAssetOwnerEntity { Kind = "systems", Key = "missing-owner", Slot = "base", Hash = hash });
        await db.SaveChangesAsync();
        var revision = await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync();
        (await new ReferenceMutationSession(db).RunAsync<bool>(_ => Task.FromResult<ErrorOr.ErrorOr<bool>>(true), default)).IsError.ShouldBeFalse();
        (await db.ReferenceAssetOwners.OrderBy(x => x.Kind).ThenBy(x => x.Key).ThenBy(x => x.Slot)
            .Select(x => x.Kind + "/" + x.Key + "/" + x.Slot + "/" + x.Hash).ToArrayAsync()).ShouldBe(expected);
        (await db.ReferenceCatalogState.Select(x => x.Revision).SingleAsync()).ShouldBe(revision);
    }

    [Theory]
    [InlineData("UPDATE romd.\"Ratings\" SET \"MinimumAge\" = -1 WHERE \"Key\" = 'Esrb:E'", "23514")]
    [InlineData("UPDATE romd.\"Companies\" SET \"Ownership\" = 'Unknown' WHERE \"Key\" = 'nintendo'", "23514")]
    [InlineData("UPDATE romd.\"Platforms\" SET \"BuiltInVersion\" = NULL WHERE \"CanonicalKey\" = 'snes'", "23514")]
    [InlineData("UPDATE romd.\"Platforms\" SET \"BaseName\" = NULL WHERE \"CanonicalKey\" = 'snes'", "23514")]
    [InlineData("UPDATE romd.\"Regions\" SET \"BaseSortOrder\" = -1 WHERE \"CanonicalKey\" = 'asia'", "23514")]
    [InlineData("UPDATE romd.\"Ratings\" SET \"BoardKey\" = 'Unknown', \"Key\" = 'Unknown:E' WHERE \"Key\" = 'Esrb:E'", "23503")]
    [InlineData("UPDATE romd.\"Platforms\" SET \"BaseAssetHash\" = repeat('0',64) WHERE \"CanonicalKey\" = 'snes'", "23503")]
    public async Task RelationalConstraints_RejectInvalidAuthorityFactsAndRelationships(string sql, string state)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        (await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState.ShouldBe(state);
    }
}
