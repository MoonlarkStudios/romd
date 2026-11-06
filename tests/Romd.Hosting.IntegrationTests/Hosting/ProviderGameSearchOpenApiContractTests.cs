using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ProviderGameSearchOpenApiContractTests(ApiContractPolicyGuardrailTests.LiveOpenApiDocuments documents)
    : IClassFixture<ApiContractPolicyGuardrailTests.LiveOpenApiDocuments>
{
    [Fact]
    public void Search_ExplicitBoundedSuggestions_ExposesCompletenessAndFixedLimitRatherThanFalsePagination()
    {
        var document = documents.Documents["admin"].RootElement;
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var operation = document.GetProperty("paths").GetProperty("/api/metadata-providers/{providerId}/games").GetProperty("get");
        var result = OpenApiTestDocuments.Resolve(operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"), schemas);

        result.GetProperty("properties").EnumerateObject().Select(x => x.Name).Order().ShouldBe(["completeness", "items", "limit"]);
        var completeness = OpenApiTestDocuments.Resolve(result.GetProperty("properties").GetProperty("completeness"), schemas);
        completeness.GetProperty("enum").EnumerateArray().ShouldAllBe(value => value.ValueKind == JsonValueKind.String);
        completeness.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ShouldBe(["Complete", "ProviderLimited", "BudgetLimited"]);
        operation.GetProperty("parameters").EnumerateArray().Where(x => x.GetProperty("in").GetString() == "query")
            .Select(x => x.GetProperty("name").GetString()).ShouldBe(["query"]);
        var description = operation.GetProperty("description").GetString().ShouldNotBeNull();
        foreach (var budget in new[] { "500", "100", "2 MiB", "10 seconds", "30 seconds", "no snapshot" })
            description.ShouldContain(budget);
    }

    [Fact]
    public void Resolution_ReturnsOneGameOnAdminOnly_AndBothOperationsPublishFailureEnvelopes()
    {
        var document = documents.Documents["admin"].RootElement;
        var paths = document.GetProperty("paths");
        var resolution = paths.GetProperty("/api/metadata-providers/{providerId}/game-resolution").GetProperty("get");
        resolution.GetProperty("operationId").GetString().ShouldBe("ResolveMetadataProviderGame");
        resolution.GetProperty("parameters").EnumerateArray().Where(x => x.GetProperty("in").GetString() == "query")
            .Select(x => x.GetProperty("name").GetString()).ShouldBe(["input"]);
        resolution.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/ProviderGameDto");
        foreach (var path in new[] { "/api/metadata-providers/{providerId}/games", "/api/metadata-providers/{providerId}/game-resolution" })
        {
            documents.Documents["consumer"].RootElement.GetProperty("paths").TryGetProperty(path, out _).ShouldBeFalse();
            foreach (var status in new[] { "400", "404", "409", "502", "503" })
                paths.GetProperty(path).GetProperty("get").GetProperty("responses").GetProperty(status).GetProperty("content")
                    .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString()
                    .ShouldBe("#/components/schemas/ProblemDetails");
        }
    }
}
