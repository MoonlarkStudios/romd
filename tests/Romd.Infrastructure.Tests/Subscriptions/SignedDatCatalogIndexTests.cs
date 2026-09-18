using System.Text.Json;
using System.Text.Json.Nodes;
using Romd.Infrastructure.Dats;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed class SignedDatCatalogIndexTests
{
    [Fact]
    public void Read_DefinitionWithoutPublishedArtifact_DoesNotOfferSubscription()
    {
        var index = Index();
        Read(index).ShouldBeEmpty();
        index["snapshot"]!["catalogs"]!["redump/psx/discs"] = JsonNode.Parse("""
            {"name":"Sony - PlayStation","artifact":null,"health":"failed","lastChanged":null}
            """);
        Read(index).ShouldBeEmpty();
    }

    [Fact]
    public void Read_UnfamiliarSystem_RemainsVisibleForExplicitResolution()
    {
        var index = Index(published: true);
        index["definitions"]!["catalogs"]!["redump/psx/discs"]!["systemId"] = "future-system";
        Read(index).Single().SystemId.ShouldBe("future-system");
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("failed")]
    public void Read_PublishedCatalog_BindsSystemAndKeepsPublisherHealth(string health)
    {
        var index = Index(published: true);
        index["snapshot"]!["catalogs"]!["redump/psx/discs"]!["health"] = health;
        var catalog = Read(index).Single();
        catalog.CatalogId.ShouldBe("redump/psx/discs");
        catalog.SystemId.ShouldBe("psx");
        catalog.Name.ShouldBe("Sony - PlayStation");
        catalog.EntryCount.ShouldBe(10974);
        catalog.FileCount.ShouldBe(60444);
        catalog.Health.ShouldBe(health);
        catalog.LastChangedAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("header")]
    [InlineData("hash")]
    public void Read_InconsistentBinding_RejectsIndex(string changed)
    {
        var index = Index(published: true);
        if (changed == "header") index["snapshot"]!["catalogs"]!["redump/psx/discs"]!["name"] = "Different catalog";
        if (changed == "hash") index["downloads"]!["objects/test.dat"]!["sha256"] = "bad";
        Should.Throw<InvalidDataException>(() => Read(index));
    }

    [Fact]
    public void Read_MixedCartridgeAndDiscPublication_OffersBothBoundCatalogs()
    {
        var index = Index(published: true);
        index["definitions"]!["catalogs"]!["no-intro/snes/standard"] = JsonNode.Parse("""
            {"systemId":"snes","provider":"no-intro","providerSystemId":"49","representation":"standard",
             "expectedName":"Nintendo - Super Nintendo Entertainment System","validation":{"minimumGames":4000,"minimumRoms":4000}}
            """);
        index["snapshot"]!["catalogs"]!["no-intro/snes/standard"] = JsonNode.Parse("""
            {"name":"Nintendo - Super Nintendo Entertainment System","artifact":{"path":"no-intro/snes/standard.dat","sha256":"snes-hash","bytes":1911272},
             "health":"healthy","counts":{"games":4358,"roms":4358},"lastChanged":"2026-09-07T00:20:00Z"}
            """);
        index["downloads"]!["no-intro/snes/standard.dat"] = JsonNode.Parse("""{"sha256":"snes-hash","bytes":1911272}""");
        var catalogs = Read(index);
        catalogs.Length.ShouldBe(2);
        var snes = catalogs.Single(c => c.SystemId == "snes");
        snes.CatalogId.ShouldBe("no-intro/snes/standard"); snes.Provider.ShouldBe("no-intro");
        snes.DocumentHash.ShouldBe("snes-hash");
        index["downloads"]!["no-intro/snes/standard.dat"]!["sha256"] = "changed";
        Should.Throw<InvalidDataException>(() => Read(index));
    }

    private static Romd.Admin.Application.Source.Dat.PublishedDatCatalog[] Read(JsonNode index)
    {
        using var document = JsonDocument.Parse(index.ToJsonString());
        return SignedDatCatalogIndex.Read(document.RootElement).ToArray();
    }
    private static JsonNode Index(bool published = false)
    {
        var index = JsonNode.Parse("""{"snapshot":{"catalogs":{}},"downloads":{}}""")!;
        index["definitions"] = JsonNode.Parse("""
            {"schemaVersion":3,"catalogs":{"redump/psx/discs":{
              "systemId":"psx","provider":"redump","providerSystemId":"psx","representation":"discs",
              "expectedName":"Sony - PlayStation","validation":{"minimumGames":10000,"minimumRoms":50000}}}}
            """);
        if (!published) return index;
        index["snapshot"]!["catalogs"]!["redump/psx/discs"] = JsonNode.Parse("""
            {"name":"Sony - PlayStation","artifact":{"path":"objects/test.dat","sha256":"test-hash","bytes":12833217},
             "health":"healthy","counts":{"games":10974,"roms":60444},"lastChanged":"2026-09-06T15:01:49.035879844Z"}
            """);
        index["downloads"]!["objects/test.dat"] = JsonNode.Parse("""{"sha256":"test-hash","bytes":12833217}""");
        return index;
    }
}
