using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class ReferenceOwnershipTests
{
    [Fact]
    public async Task Import_PreservesUnregisteredOperationalRows_WithoutAdoptingThem()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.Platforms.Add(new Romd.Persistence.Entities.PlatformEntity { Name = "Unregistered platform", ShortName = "unknown" });
        db.Regions.Add(new Romd.Persistence.Entities.RegionEntity { Name = "Unregistered region", IsAutoCreated = true, CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Unregistered region") });
        db.GameLanguages.Add(new Romd.Persistence.Entities.GameLanguageEntity { Name = "Unregistered language", Code = "unknown", IsAutoCreated = true, CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Unregistered language") });
        await db.SaveChangesAsync();
        var systemId = await db.Platforms.Select(x => x.Id).SingleAsync();
        var regionId = await db.Regions.Select(x => x.Id).SingleAsync();
        var languageId = await db.GameLanguages.Select(x => x.Id).SingleAsync();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        (await db.Platforms.SingleAsync(x => x.Id == systemId)).CanonicalKey.ShouldBeNull();
        // Unregistered rows keep their minted key and stay unregistered; a key is not a registration.
        var region = await db.Regions.SingleAsync(x => x.Id == regionId);
        (region.CanonicalKey, region.Ownership).ShouldBe(("dat-unregistered-region", null));
        var language = await db.GameLanguages.SingleAsync(x => x.Id == languageId);
        (language.CanonicalKey, language.Ownership).ShouldBe(("dat-unregistered-language", null));
        (await new ReferenceCatalogService(db, TestReferenceBlobs.For(db)).GetCurrentAsync(default))!.Regions.ShouldNotContain(x => x.Key.StartsWith("dat-"));
        (await new ReferenceCatalogService(db, TestReferenceBlobs.For(db)).GetCurrentAsync(default))!.Systems.ShouldNotContain(x => x.Name == "Unregistered platform");
    }

    [Fact]
    public async Task ExplicitClears_SurviveCatalogImport_ResetRevealsCurrentDefaults()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var commands = new TypedReferenceTestCommands(db);
        var system = (await commands.System("snes"))!;
        var company = (await commands.Company("nintendo"))!;
        (await commands.UpdateSystem("snes", new SystemPatch { Name = "Local SNES", Description = null, Icon = null }, system.Revision)).IsError.ShouldBeFalse();
        (await commands.UpdateCompany("nintendo", new(Name: "Local maker", Description: null, HasDescription: true), company.Revision)).IsError.ShouldBeFalse();
        var next = JsonNode.Parse(BundledReferenceData.Document.GetRawText())!;
        next["catalogVersion"] = next["catalogVersion"]!.GetValue<int>() + 1;
        next["systems"]!["snes"]!["description"] = "New system default";
        next["companies"]!["nintendo"]!["description"] = "New company default";
        next["companies"]!["nintendo"]!["name"] = "New maker default";
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System, document: JsonSerializer.SerializeToElement(next));
        db.ChangeTracker.Clear();
        var row = await db.Platforms.SingleAsync(x => x.CanonicalKey == "snes");
        row.HasArtworkOverride.ShouldBeTrue();
        row.ArtworkOverrideHash.ShouldBeNull();
        row.BaseAssetHash.ShouldNotBeNull();
        row.HasDescriptionOverride.ShouldBeTrue();
        row.DescriptionOverride.ShouldBeNull();
        (await db.Companies.Where(x => x.Name == "Local maker").Select(x => x.Key).SingleAsync()).ShouldBe("nintendo");
        (await commands.System("snes"))!.Resource.Description.ShouldBeNull();
        (await commands.Company("nintendo"))!.Resource.Description.ShouldBeNull();
        (await commands.ResetSystem("snes", null, (await commands.System("snes"))!.Revision)).IsError.ShouldBeFalse();
        (await commands.ResetCompany("nintendo", null, (await commands.Company("nintendo"))!.Revision)).IsError.ShouldBeFalse();
        var snapshot = (await new ReferenceCatalogService(db, TestReferenceBlobs.For(db)).GetCurrentAsync(default))!;
        var reset = (await commands.System("snes"))!.Resource;
        reset.Description.ShouldBe("New system default");
        reset.Icon.ShouldNotBeNull();
        var effectiveCompany = (await commands.Company("nintendo"))!.Resource;
        effectiveCompany.Description.ShouldBe("New company default");
        effectiveCompany.Name.ShouldBe("New maker default");
        (await db.Companies.Where(x => x.Name == effectiveCompany.Name).Select(x => x.Key).SingleAsync()).ShouldBe("nintendo");
        (await db.Platforms.Where(x => x.Name == reset.Name).Select(x => x.CanonicalKey).SingleAsync()).ShouldBe("snes");
        snapshot.Systems.Single(x => x.Key == "snes").Icon.ShouldBe(reset.Icon);
        snapshot.Companies.Single(x => x.Key == "nintendo").Name.ShouldBe(effectiveCompany.Name);
        reset.Manufacturers.Single(x => x.Key == "nintendo").Name.ShouldBe(effectiveCompany.Name);
    }
}
