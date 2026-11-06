using System.Text.Json;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ReferenceOpenApiContractTests
{
    [Fact]
    public void AdminContract_ExposesTypedFactsAndRelationshipArrays_WithoutClosingSystemKeys()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "web/schemas/admin-v1.json")))
            directory = directory.Parent;
        directory.ShouldNotBeNull();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "web/schemas/admin-v1.json")));
        var paths = document.RootElement.GetProperty("paths");
        // ROMD owns region, language and rating facts: no route edits or overrides them. Regions and languages
        // manage only their aliases and merges of unregistered rows; rating boards are read-only.
        foreach (var path in paths.EnumerateObject().Where(x => x.Name.StartsWith("/api/regions", StringComparison.Ordinal)
            || x.Name.StartsWith("/api/languages", StringComparison.Ordinal) || x.Name.StartsWith("/api/rating-boards", StringComparison.Ordinal)))
        {
            path.Name.ShouldNotContain("overrides");
            var methods = path.Value.EnumerateObject().Select(x => x.Name).ToArray();
            methods.ShouldNotContain("put");
            methods.ShouldNotContain("patch");
            if (path.Name.StartsWith("/api/rating-boards", StringComparison.Ordinal) || !path.Name.Contains("/aliases") && !path.Name.Contains("/merge"))
                methods.ShouldBe(["get"], path.Name);
        }
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var patch = schemas.GetProperty("SystemPatchDto");
        var keys = patch.GetProperty("properties").GetProperty("manufacturerKeys");
        keys.GetProperty("type").GetString().ShouldBe("array");
        keys.GetProperty("items").GetProperty("type").GetString().ShouldBe("string");
        patch.TryGetProperty("required", out var required).ShouldBeFalse();
        var systemKey = schemas.GetProperty("SystemResourceDto").GetProperty("properties").GetProperty("key");
        systemKey.GetProperty("type").GetString().ShouldBe("string");
        systemKey.TryGetProperty("enum", out _).ShouldBeFalse();
        var company = schemas.GetProperty("CompanyResourceDto").GetProperty("properties");
        company.TryGetProperty("compactLabel", out _).ShouldBeFalse();
        company.TryGetProperty("minimumAge", out _).ShouldBeFalse();
    }
    [Theory]
    [InlineData("admin")]
    [InlineData("consumer")]
    public void Contract_PagesReferencesAndSnapshotFamilies_AndPublishesDistinctManifestAndRowValidators(string host)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "web/schemas/" + host + "-v1.json")))
            directory = directory.Parent;
        directory.ShouldNotBeNull();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "web/schemas/" + host + "-v1.json")));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/systems", "/api/companies" }.Concat(host == "admin" ? ["/api/system-setup"] : Array.Empty<string>()))
        {
            var get = paths.GetProperty(path).GetProperty("get");
            var schema = get.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            var reference = schema.GetProperty("$ref").GetString();
            reference.ShouldNotBeNull();
            reference.ShouldContain("Page");
            var parameters = get.GetProperty("parameters").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
            parameters.Keys.ShouldContain("cursor");
            parameters.Keys.ShouldContain("sortBy");
            parameters.Keys.ShouldContain("query");
            parameters["limit"].GetProperty("schema").GetProperty("maximum").GetInt32().ShouldBe(100);
        }
        foreach (var family in new[] { "systems", "companies", "regions", "languages", "rating-boards", "ratings" })
        {
            var get = paths.GetProperty("/api/catalog-snapshot/" + family).GetProperty("get");
            var parameters = get.GetProperty("parameters").EnumerateArray().ToArray();
            parameters.Single(x => x.GetProperty("name").GetString() == "revision").GetProperty("required").GetBoolean().ShouldBeTrue();
            var responses = get.GetProperty("responses");
            foreach (var status in new[] { "200", "400", "409", "503" })
                responses.TryGetProperty(status, out _).ShouldBeTrue();
            responses.TryGetProperty("304", out _).ShouldBeFalse();
        }
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "SystemResourceDto", "CompanyResourceDto" })
            schemas.GetProperty(name).GetProperty("required").EnumerateArray().Select(x => x.GetString()).ShouldContain("revision");
        schemas.GetProperty("ReferenceCatalogDescriptorDto").GetProperty("properties").TryGetProperty("systems", out _).ShouldBeFalse();
        schemas.GetProperty("ReferenceCatalogCountsDto").GetProperty("properties").EnumerateObject().Count().ShouldBe(6);
        schemas.GetProperty("SystemResourceDto").GetProperty("properties").GetProperty("manufacturers").GetProperty("maxItems").GetInt32().ShouldBe(16);
        if (host == "admin")
            schemas.GetProperty("ManagedSystemDto").GetProperty("properties").TryGetProperty("aliases", out _).ShouldBeFalse();
    }

}
