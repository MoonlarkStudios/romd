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
        foreach (var path in paths.EnumerateObject().Where(x => x.Name.StartsWith("/api/regions", StringComparison.Ordinal)
            || x.Name.StartsWith("/api/languages", StringComparison.Ordinal) || x.Name.StartsWith("/api/rating-boards", StringComparison.Ordinal)))
        {
            path.Name.ShouldNotContain("overrides");
            path.Value.EnumerateObject().Select(x => x.Name).ShouldBe(new[] { "get" });
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
}
