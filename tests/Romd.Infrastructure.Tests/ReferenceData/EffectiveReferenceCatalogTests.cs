using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class EffectiveReferenceCatalogTests
{
    private static RomdDbContext Open(PostgreSqlTestDatabase database) => new(new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(database.ConnectionString).Options);
    private static SystemPatchDto Patch(string json) => JsonSerializer.Deserialize<SystemPatchDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;


    private static JsonObject NextCatalog() {
        var catalog = JsonNode.Parse(BundledReferenceData.Document.GetRawText())!.AsObject();
        catalog["catalogVersion"] = catalog["catalogVersion"]!.GetValue<int>() + 1;
        return catalog;
    }
    private static JsonElement Document(JsonObject catalog) => JsonSerializer.SerializeToElement(catalog);

    [Fact]
    public async Task Upgrade_PreservesOverrides_ResetRevealsNewDefaults_AndDowngradeIsAtomic()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var before = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        var edited = await new TypedReferenceTestCommands(db).UpdateSystem("snes", Patch("""{"name":"My SNES","icon":null}"""), before.ETag, default);
        edited.IsError.ShouldBeFalse();
        var next = NextCatalog();
        next["systems"]!["snes"]!["name"] = "Updated Super Nintendo";
        next["systems"]!["snes"]!["compactLabel"] = "Super NES";
        next["systems"]!["snes"]!["iconPath"] = next["systems"]!["nes"]!["iconPath"]!.DeepClone();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance, document: Document(next));
        var upgraded = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        upgraded.Resource.Name.ShouldBe("My SNES");
        upgraded.Resource.CompactLabel.ShouldBe("Super NES");
        upgraded.Resource.Icon.ShouldBeNull();
        var reset = await new TypedReferenceTestCommands(db).ResetSystem("snes", null, upgraded.ETag, default);
        reset.Value.Resource.Name.ShouldBe("Updated Super Nintendo");
        reset.Value.Resource.Icon.ShouldNotBeNull();
        reset.Value.Resource.Icon!.Sha256.ShouldNotBe(before.Resource.Icon!.Sha256);
        var snapshot = (await service.GetCurrentAsync(default))!;
        var facts = snapshot.Systems.Single(x => x.Key == "snes");
        var id = (await service.ResolveSystemIdAsync("snes", default))!.Value;
        var summary = (await new Romd.Persistence.Queries.SystemSummaryReader(db).ReadAsync([id], default))[id];
        summary.Name.ShouldBe(facts.Name);
        summary.Icon!.Url.ShouldBe(facts.Icon!.Url);
        reset.Value.Resource.Icon.Url.ShouldBe(facts.Icon.Url);
        await Should.ThrowAsync<InvalidDataException>(() => SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance));
        db.ChangeTracker.Clear();
        (await service.GetCurrentAsync(default))!.Revision.ShouldBe(snapshot.Revision);
        (await new TypedReferenceTestCommands(db).System("snes", default))!.Resource.Name.ShouldBe(facts.Name);
    }

    [Fact]
    public async Task Removal_IsRejected_ExplicitRetirementPreservesIdentityAndReferences()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var id = (await service.ResolveSystemIdAsync("snes", default))!.Value;
        var next = NextCatalog();
        next["systems"]!.AsObject().Remove("snes");
        await Should.ThrowAsync<InvalidDataException>(() => SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance, document: Document(next)));
        next = NextCatalog();
        next["systems"]!["snes"]!["retired"] = true;
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance, document: Document(next));
        (await service.ResolveSystemIdAsync("snes", default)).ShouldBe(id);
        (await new TypedReferenceTestCommands(db).System("snes", default))!.Resource.Retired.ShouldBeTrue();
        (await service.GetCurrentAsync(default))!.Systems.Single(x => x.Key == "snes").Retired.ShouldBeTrue();
        (await db.PlatformAliases.AnyAsync(x => x.PlatformId == id)).ShouldBeTrue();
    }

    [Fact]
    public async Task SystemSummaries_UseEffectiveFactsAndStableKeys_ForKnownAndUnfamiliarSystems()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var original = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        var created = await new TypedReferenceTestCommands(db).CreateSystem(new("local-future-console", "Future Console", CompactLabel: "FUT", Icon: original.Resource.Icon!.Sha256), default);
        created.IsError.ShouldBeFalse();
        var snesId = (await service.ResolveSystemIdAsync("snes", default))!.Value;
        var futureId = (await service.ResolveSystemIdAsync("local-future-console", default))!.Value;
        var reader = new Romd.Persistence.Queries.SystemSummaryReader(db);
        var before = await reader.ReadAsync([snesId, futureId, snesId], default);
        before.Count.ShouldBe(2);
        before[futureId].Key.ShouldBe("local-future-console");
        before[futureId].CompactLabel.ShouldBe("FUT");
        before[futureId].Icon!.Url.ShouldBe(original.Resource.Icon.Url);
        var edited = await new TypedReferenceTestCommands(db).UpdateSystem("snes", Patch("""{"name":"My Nintendo","compactLabel":"SUPER","icon":null}"""), original.ETag, default);
        edited.IsError.ShouldBeFalse();
        var after = (await reader.ReadAsync([snesId], default))[snesId];
        after.Key.ShouldBe("snes");
        after.Name.ShouldBe("My Nintendo");
        after.CompactLabel.ShouldBe("SUPER");
        after.Icon.ShouldBeNull();
    }

    [Fact]
    public async Task Managed_PatchAndReset_PreserveInheritanceAndRejectStaleWrites()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var before = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        var snapshot = (await service.GetCurrentAsync(default))!;
        var edited = await new TypedReferenceTestCommands(db).UpdateSystem("snes", Patch("""{"compactLabel":"SUPER","icon":null}"""), before.ETag, default);
        edited.IsError.ShouldBeFalse();
        edited.Value.Resource.Name.ShouldBe(before.Resource.Name);
        edited.Value.Resource.Icon.ShouldBeNull();
        edited.Value.Overrides.CompactLabel.ShouldBe("SUPER");
        edited.Value.Overrides.HasIcon.ShouldBeTrue();
        edited.Value.Overrides.Icon.ShouldBeNull();
        edited.Value.Overrides.Name.ShouldBeNull();
        (await new TypedReferenceTestCommands(db).UpdateSystem("snes", Patch("""{"name":"Lost edit"}"""), before.ETag, default)).FirstError.Code.ShouldBe("ReferenceResource.PreconditionFailed");
        (await new TypedReferenceTestCommands(db).UpdateSystem("snes", Patch("{}"), null, default)).FirstError.Code.ShouldBe("ReferenceResource.PreconditionRequired");
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var reseeded = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        reseeded.Resource.CompactLabel.ShouldBe("SUPER");
        var reset = await new TypedReferenceTestCommands(db).ResetSystem("snes", SystemOverrideField.Icon, reseeded.ETag, default);
        reset.Value.Resource.Icon.ShouldBe(before.Resource.Icon);
        reset.Value.Resource.CompactLabel.ShouldBe("SUPER");
        var cleared = await new TypedReferenceTestCommands(db).ResetSystem("snes", null, reset.Value.ETag, default);
        JsonSerializer.Serialize(cleared.Value.Resource).ShouldBe(JsonSerializer.Serialize(before.Resource));
        JsonSerializer.Serialize((await service.GetCurrentAsync(default))!.Systems).ShouldBe(JsonSerializer.Serialize(snapshot.Systems));
    }

    [Fact]
    public async Task InstallationOwned_EditUpdatesBase_AndDeletionRequiresNoDependents()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var created = await new TypedReferenceTestCommands(db).CreateSystem(new("local-future-system", "Future System", CompactLabel: "FUT"), default);
        created.Value.Resource.Ownership.ShouldBe("Installation");
        var edited = await new TypedReferenceTestCommands(db).UpdateSystem("local-future-system", Patch("""{"name":"Changed"}"""), created.Value.ETag, default);
        edited.Value.Overrides.ShouldBe(new SystemOverridesDto(null, null, null, null, null, false, false));
        var row = await db.Platforms.SingleAsync(x => x.CanonicalKey == "local-future-system");
        row.Ownership.ShouldBe(ReferenceOwnership.Installation);
        row.BaseName.ShouldBe("Changed");
        row.ReferenceOverrides.ShouldBe(new SystemOverrides());
        (await new TypedReferenceTestCommands(db).ResetSystem("local-future-system", null, edited.Value.ETag, default)).IsError.ShouldBeTrue();
        var id = (await service.ResolveSystemIdAsync("local-future-system", default))!.Value;
        db.PlatformAliases.Add(PlatformAliasEntity.FromDomain(Romd.Domain.Source.Platform.PlatformAlias.CreateName(id, "dependency")));
        await db.SaveChangesAsync();
        (await new TypedReferenceTestCommands(db).DeleteSystem("local-future-system", edited.Value.ETag, default)).FirstError.Code.ShouldBe("ReferenceResource.Conflict");
        db.PlatformAliases.RemoveRange(await db.PlatformAliases.AsTracking().Where(x => x.PlatformId == id).ToListAsync());
        await db.SaveChangesAsync();
        (await new TypedReferenceTestCommands(db).DeleteSystem("local-future-system", edited.Value.ETag, default)).IsError.ShouldBeFalse();
        (await new TypedReferenceTestCommands(db).System("local-future-system", default)).ShouldBeNull();
        var builtin = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        (await new TypedReferenceTestCommands(db).DeleteSystem("snes", builtin.ETag, default)).FirstError.Code.ShouldBe("ReferenceResource.Managed");
    }

    [Fact]
    public async Task Snapshot_IncludesEveryReferenceFamily_AndReseedingIsDeterministic()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var before = (await service.GetCurrentAsync(default))!;
        before.Companies.ShouldNotBeEmpty(); before.Regions.ShouldNotBeEmpty(); before.Languages.ShouldNotBeEmpty();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        (await service.GetCurrentAsync(default))!.Revision.ShouldBe(before.Revision);
        (await service.AddAssetAsync("<svg><script/></svg>"u8.ToArray(), "image/svg+xml", default)).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentEditors_OnlyOnePatchWithTheSameETagWins()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var setup = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(setup, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var original = (await new TypedReferenceTestCommands(setup).System("snes", default))!;
        await using var first = Open(database);
        await using var second = Open(database);
        var results = await Task.WhenAll(
            new TypedReferenceTestCommands(first).UpdateSystem("snes", new SystemPatchDto { Name = "First editor" }, original.ETag, default),
            new TypedReferenceTestCommands(second).UpdateSystem("snes", new SystemPatchDto { Name = "Second editor" }, original.ETag, default));
        results.Count(result => !result.IsError).ShouldBe(1);
        results.Single(result => result.IsError).FirstError.Code.ShouldBe("ReferenceResource.PreconditionFailed");
    }

    [Fact]
    public async Task ManufacturerRelationships_RequireRegisteredKeys_AndPreventCompanyDeletion()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        (await new TypedReferenceTestCommands(db).CreateSystem(new("local-unresolved", "Unknown", "UNK", ManufacturerKeys: ["unknown-company"]), default)).IsError.ShouldBeTrue();
        (await new TypedReferenceTestCommands(db).System("local-unresolved", default)).ShouldBeNull();
        var company = await new TypedReferenceTestCommands(db).CreateCompany(new("local-custom-company", "Custom Company"), default);
        var system = await new TypedReferenceTestCommands(db).CreateSystem(new("local-custom-system", "Custom System", "CUS", ManufacturerKeys: ["local-custom-company"]), default);
        system.IsError.ShouldBeFalse();
        system.Value.Resource.Manufacturers.Select(x => x.Key).ShouldBe(new[] { "local-custom-company" });
        (await new TypedReferenceTestCommands(db).DeleteCompany("local-custom-company", company.Value.ETag, default)).FirstError.Code.ShouldBe("ReferenceResource.Conflict");
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData("has spaces", "Name")]
    [InlineData("-leading", "Name")]
    [InlineData("trailing-", "Name")]
    [InlineData("valid", " ")]
    public async Task Creation_RejectsInvalidIdentitiesAndLabels(string key, string name)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        (await new TypedReferenceTestCommands(db).CreateSystem(new(key, name, name), default)).IsError.ShouldBeTrue();
        (await db.Platforms.AnyAsync(x => x.Ownership != null)).ShouldBeFalse();
    }

    [Fact]
    public void Patch_RoundTrip_PreservesOnlySuppliedFieldsIncludingNull()
    {
        var patch = new SystemPatchDto { Name = "Changed", Icon = null };
        var roundTrip = Patch(JsonSerializer.Serialize(patch));
        roundTrip.Changes.Keys.Order().ShouldBe(new[] { "icon", "name" });
        roundTrip.Changes["icon"].ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"compactLabel\":null}")]
    [InlineData("{\"monochrome\":null}")]
    public async Task Patch_InvalidNull_DoesNotChangeResource(string json)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Open(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var before = (await new TypedReferenceTestCommands(db).System("snes", default))!;
        Should.Throw<JsonException>(() => Patch(json));
        (await new TypedReferenceTestCommands(db).System("snes", default))!.ETag.ShouldBe(before.ETag);
    }

    [Theory]
    [InlineData("{\"key\":\"new-key\"}")]
    [InlineData("{\"minimumAge\":0}")]
    [InlineData("{\"unexpected\":true}")]
    public void Patch_UnknownOrImmutableField_IsRejected(string json) => Should.Throw<JsonException>(() => Patch(json));
}
