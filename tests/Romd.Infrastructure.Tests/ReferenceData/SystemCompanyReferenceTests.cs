using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class SystemCompanyReferenceTests
{
    [Fact]
    public async Task CompanyRenameAndReset_PropagateThroughRelationshipsAndPublication_RejectStaleSystemEdits()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var commands = new TypedReferenceTestCommands(db);
        var system = (await commands.System("snes"))!;
        var manufacturer = system.Resource.Manufacturers.ShouldHaveSingleItem();
        var company = (await commands.Company(manufacturer.Key))!;
        var changed = await commands.UpdateCompany(manufacturer.Key, new(Name: "Local Nintendo"), company.ETag);
        changed.IsError.ShouldBeFalse();
        (await commands.UpdateCompany(manufacturer.Key, new(Name: "Stale"), company.ETag)).FirstError.Code.ShouldBe("ReferenceResource.PreconditionFailed");
        (await commands.UpdateSystem("snes", new SystemPatchDto { Name = "Stale system" }, system.ETag)).FirstError.Code.ShouldBe("ReferenceResource.PreconditionFailed");
        await AssertManufacturer("Local Nintendo");
        var reset = await commands.ResetCompany(manufacturer.Key, CompanyOverrideField.Name, changed.Value.ETag);
        reset.IsError.ShouldBeFalse();
        await AssertManufacturer(manufacturer.Name);

        async Task AssertManufacturer(string expected)
        {
            var current = (await commands.System("snes"))!;
            current.Resource.Manufacturers.ShouldHaveSingleItem().Name.ShouldBe(expected);
            var snapshot = (await new ReferenceCatalogService(db, TestReferenceBlobs.Instance).GetCurrentAsync(default))!;
            snapshot.Systems.Single(x => x.Key == "snes").Manufacturers.ShouldBe(current.Resource.Manufacturers);
            snapshot.Companies.Single(x => x.Key == manufacturer.Key).Name.ShouldBe(expected);
            var platform = await db.Platforms.SingleAsync(x => x.CanonicalKey == "snes");
            platform.Manufacturer.ShouldBe(expected);
            var relationshipFacts = await new SystemCompanyReader(db).ReadAsync([platform.Id], default);
            relationshipFacts[platform.Id].ShouldBe(current.Resource.Manufacturers);
            (await new Romd.Persistence.Repositories.PlatformRepository(db).GetByIdAsync(platform.Id))!.Manufacturer.ShouldBe(expected);
        }
    }

    [Fact]
    public async Task BuiltInUpgrade_PreservesCompanyOverridesAndSystemIdentity_RefreshesExplicitRelationships()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var commands = new TypedReferenceTestCommands(db);
        var system = (await commands.System("snes"))!;
        var companyKey = system.Resource.Manufacturers.ShouldHaveSingleItem().Key;
        var company = (await commands.Company(companyKey))!;
        (await commands.UpdateCompany(companyKey, new(Name: "My manufacturer"), company.ETag)).IsError.ShouldBeFalse();
        (await commands.UpdateSystem("snes", new SystemPatchDto { CompactLabel = "MY SNES" }, system.ETag)).IsError.ShouldBeTrue();
        system = (await commands.System("snes"))!;
        (await commands.UpdateSystem("snes", new SystemPatchDto { CompactLabel = "MY SNES" }, system.ETag)).IsError.ShouldBeFalse();
        var id = (await db.Platforms.SingleAsync(x => x.CanonicalKey == "snes")).Id;
        var next = JsonNode.Parse(BundledReferenceData.Document.GetRawText())!;
        next["catalogVersion"] = next["catalogVersion"]!.GetValue<int>() + 1;
        next["companies"]![companyKey]!["name"] = "New Nintendo default";
        next["systems"]!["snes"]!["name"] = "New Super Nintendo default";
        next["companies"]!["new-partner"] = JsonSerializer.SerializeToNode(new { name = "Partner" });
        next["systems"]!["snes"]!["manufacturerIds"] = new JsonArray(companyKey, "new-partner");
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance, document: JsonSerializer.SerializeToElement(next));
        var upgraded = (await commands.System("snes"))!;
        upgraded.Resource.Name.ShouldBe("New Super Nintendo default");
        upgraded.Resource.CompactLabel.ShouldBe("MY SNES");
        upgraded.Resource.Manufacturers.Select(x => x.Key).ShouldBe(new[] { companyKey, "new-partner" }.Order(StringComparer.Ordinal));
        upgraded.Resource.Manufacturers.Single(x => x.Key == companyKey).Name.ShouldBe("My manufacturer");
        (await db.Platforms.SingleAsync(x => x.CanonicalKey == "snes")).Id.ShouldBe(id);
        var currentCompany = (await commands.Company(companyKey))!;
        (await commands.ResetCompany(companyKey, null, currentCompany.ETag)).Value.Resource.Name.ShouldBe("New Nintendo default");
        (await commands.System("snes"))!.Resource.Manufacturers.Single(x => x.Key == companyKey).Name.ShouldBe("New Nintendo default");
        (await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'romd' AND tablename = 'ReferenceDefinitions'").ToListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task CustomRelationships_AreExplicitlyEditable_ProtectCompanyDeletion_AndDoNotEnableRuntime()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var commands = new TypedReferenceTestCommands(db);
        (await commands.CreateCompany(new("unreserved", "Invalid"))).IsError.ShouldBeTrue();
        (await commands.CreateSystem(new("unreserved", "Invalid", "INV"))).IsError.ShouldBeTrue();
        var company = (await commands.CreateCompany(new("local-maker", "Maker"))).Value;
        var system = (await commands.CreateSystem(new("local-machine", "Machine", "MCH", ManufacturerKeys: ["local-maker"]))).Value;
        var id = (await db.Platforms.SingleAsync(x => x.CanonicalKey == "local-machine")).Id;
        (await db.Platforms.SingleAsync(x => x.Id == id)).IsEnabled.ShouldNotBe(true);
        (await db.PlatformAliases.AnyAsync(x => x.PlatformId == id)).ShouldBeFalse();
        var renamed = (await commands.UpdateCompany("local-maker", new(Name: "Renamed"), company.ETag)).Value;
        renamed.Overrides.ShouldBe(new CompanyOverridesDto(null, null, false));
        (await commands.DeleteCompany("local-maker", renamed.ETag)).FirstError.Code.ShouldBe("ReferenceResource.Conflict");
        system = (await commands.System("local-machine"))!;
        var detached = await commands.UpdateSystem("local-machine", new SystemPatchDto { ManufacturerKeys = [] }, system.ETag);
        detached.IsError.ShouldBeFalse();
        detached.Value.Resource.Manufacturers.ShouldBeEmpty();
        (await commands.DeleteCompany("local-maker", renamed.ETag)).IsError.ShouldBeFalse();
        (await commands.DeleteSystem("local-machine", detached.Value.ETag)).IsError.ShouldBeFalse();
        (await commands.Company("local-maker")).ShouldBeNull();
        (await commands.System("local-machine")).ShouldBeNull();
    }

    [Fact]
    public async Task PublicationFailure_RollsBackFactsProjectionsAndSnapshotTogether()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var commands = new TypedReferenceTestCommands(db);
        var system = (await commands.System("snes"))!;
        var key = system.Resource.Manufacturers[0].Key;
        var company = (await commands.Company(key))!;
        var catalog = new ReferenceCatalogService(db, TestReferenceBlobs.Instance);
        var revision = (await catalog.GetCurrentAsync(default))!.Revision;
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION romd.reject_reference_publication() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'simulated publication failure'; END $$;
            CREATE TRIGGER reject_reference_publication BEFORE UPDATE ON romd."ReferenceCatalogState"
                FOR EACH ROW EXECUTE FUNCTION romd.reject_reference_publication();
            """);
        await Should.ThrowAsync<DbUpdateException>(() =>
            commands.UpdateCompany(key, new(Name: "Must roll back"), company.ETag));
        (await commands.Company(key))!.ETag.ShouldBe(company.ETag);
        (await commands.System("snes"))!.ETag.ShouldBe(system.ETag);
        (await db.Platforms.SingleAsync(x => x.CanonicalKey == "snes")).Manufacturer.ShouldBe(company.Resource.Name);
        (await catalog.GetCurrentAsync(default))!.Revision.ShouldBe(revision);
    }

    [Fact]
    public async Task CompanyRetirement_IsExplicit_PreservesRelationshipsAndOverrides()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var commands = new TypedReferenceTestCommands(db);
        var key = (await commands.System("snes"))!.Resource.Manufacturers[0].Key;
        var company = (await commands.Company(key))!;
        (await commands.UpdateCompany(key, new(Name: "Local name"), company.ETag)).IsError.ShouldBeFalse();
        var next = JsonNode.Parse(BundledReferenceData.Document.GetRawText())!;
        next["catalogVersion"] = next["catalogVersion"]!.GetValue<int>() + 1;
        var definition = next["companies"]![key]!.DeepClone();
        next["companies"]!.AsObject().Remove(key);
        await Should.ThrowAsync<InvalidDataException>(() =>
            SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance, document: JsonSerializer.SerializeToElement(next)));
        definition["retired"] = true;
        next["companies"]![key] = definition;
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance, document: JsonSerializer.SerializeToElement(next));
        (await commands.Company(key))!.Resource.Retired.ShouldBeTrue();
        (await commands.System("snes"))!.Resource.Manufacturers.ShouldContain(x => x.Key == key && x.Name == "Local name");
    }

    [Fact]
    public async Task TypedMigration_PreservesIdsOverridesRelationshipsAndAssetOwners()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var commands = new TypedReferenceTestCommands(db);
        var original = (await commands.System("snes"))!;
        (await commands.UpdateSystem("snes", new SystemPatchDto { Name = "My SNES", Icon = null }, original.ETag)).IsError.ShouldBeFalse();
        var company = (await commands.Company(original.Resource.Manufacturers[0].Key))!;
        (await commands.UpdateCompany(company.Resource.Key, new(Name: "My Nintendo"), company.ETag)).IsError.ShouldBeFalse();
        var before = (await commands.System("snes"))!;
        var ids = await db.Platforms.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        var owners = await db.ReferenceAssetOwners.OrderBy(x => x.Kind).ThenBy(x => x.Key).ThenBy(x => x.Slot)
            .Select(x => x.Kind + "/" + x.Key + "/" + x.Slot + "/" + x.Hash).ToArrayAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260917141120_ReferenceCatalog");
        db.ChangeTracker.Clear();
        // At the previous schema, these are genuine generic rows with flat override JSON.
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM romd.\"ReferenceDefinitions\" WHERE \"Kind\" IN ('systems', 'companies')").SingleAsync()).ShouldBeGreaterThan(56);
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var after = (await commands.System("snes"))!;
        JsonSerializer.Serialize(after).ShouldBe(JsonSerializer.Serialize(before));
        (await db.Platforms.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync()).ShouldBe(ids);
        (await db.ReferenceAssetOwners.OrderBy(x => x.Kind).ThenBy(x => x.Key).ThenBy(x => x.Slot)
            .Select(x => x.Kind + "/" + x.Key + "/" + x.Slot + "/" + x.Hash).ToArrayAsync()).ShouldBe(owners);
        (await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'romd' AND tablename = 'ReferenceDefinitions'").ToListAsync()).ShouldBeEmpty();
    }
}
