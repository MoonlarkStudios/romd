using System.Text.Json;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class S6ClosedFilterOpenApiTests(ApiContractPolicyGuardrailTests.LiveOpenApiDocuments documents)
    : IClassFixture<ApiContractPolicyGuardrailTests.LiveOpenApiDocuments>
{
    [Fact]
    public void AdminFilters_NamedAndLambdaHandlers_PublishClosedVocabulariesAndPreserveDefaults()
    {
        var roms = Get("admin", "/api/roms");
        Values(roms, "status").ShouldBe(["cataloged", "unrouted", "unidentified"]);
        Parameter(roms, "limit").GetProperty("default").GetInt32().ShouldBe(50);
        var workbench = Get("admin", "/api/enrichment/workbench");
        Values(workbench, "view").ShouldBe(["NeedsReview", "Automatic", "Processing", "Blocked", "All"]);
        Parameter(workbench, "view").GetProperty("default").GetString().ShouldBe("NeedsReview");
        Parameter(workbench, "query").GetProperty("maxLength").GetInt32().ShouldBe(200);
    }

    [Fact]
    public void ConsumerFilters_PublishClosedVocabulariesAndPreservePageLimits()
    {
        var catalog = Get("consumer", "/api/catalog");
        Values(catalog, "sortBy").ShouldBe(["name", "rating"]);
        Values(catalog, "completeness").ShouldBe(["all", "complete", "partial"]);
        var limit = Parameter(catalog, "limit");
        limit.GetProperty("default").GetInt32().ShouldBe(50);
        limit.GetProperty("minimum").GetInt32().ShouldBe(1);
        limit.GetProperty("maximum").GetInt32().ShouldBe(100);
    }

    private JsonElement Get(string host, string path) =>
        documents.Documents[host].RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");

    private static JsonElement Parameter(JsonElement operation, string name) =>
        operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == name)
            .GetProperty("schema");

    private static IEnumerable<string?> Values(JsonElement operation, string name) =>
        Parameter(operation, name).GetProperty("enum").EnumerateArray().Select(value => value.GetString());
}
