using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     The published precondition mechanism
///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"): concurrency-controlled writes
///     declare a required If-Match header, 400, 412 and 428 problems and the ETag of their success response; creates
///     declare only that ETag; their GETs declare the ETag and, when they revalidate, 304.
/// </summary>
public sealed class PreconditionOpenApiContractTests(ApiContractPolicyGuardrailTests.LiveOpenApiDocuments documents)
    : IClassFixture<ApiContractPolicyGuardrailTests.LiveOpenApiDocuments>
{
    private const string ProblemDetailsReference = "#/components/schemas/ProblemDetails";

    [Theory]
    [InlineData("admin", "/api/systems/{systemKey}", "patch", true)]
    [InlineData("admin", "/api/systems/{systemKey}/enrichment/defaults", "patch", true)]
    [InlineData("admin", "/api/systems/{systemKey}/overrides", "delete", true)]
    [InlineData("admin", "/api/systems/{systemKey}/overrides/{field}", "delete", true)]
    [InlineData("admin", "/api/systems/{systemKey}", "delete", false)]
    [InlineData("admin", "/api/companies/{companyKey}", "patch", true)]
    [InlineData("admin", "/api/companies/{companyKey}/overrides", "delete", true)]
    [InlineData("admin", "/api/companies/{companyKey}/overrides/{field}", "delete", true)]
    [InlineData("admin", "/api/companies/{companyKey}", "delete", false)]
    [InlineData("admin", "/api/titles/{titleId}", "patch", true)]
    [InlineData("admin", "/api/titles/{titleId}/content-ratings/{board}", "put", true)]
    [InlineData("admin", "/api/titles/{titleId}/content-ratings/{board}", "delete", true)]
    [InlineData("admin", "/api/titles/{titleId}/enrichment", "patch", true)]
    [InlineData("admin", "/api/titles/{titleId}/enrichment-omissions/{field}", "put", true)]
    [InlineData("admin", "/api/titles/{titleId}/media", "post", true)]
    [InlineData("admin", "/api/titles/{titleId}/media/{mediaId}", "patch", true)]
    [InlineData("admin", "/api/titles/{titleId}/media/{mediaId}", "delete", true)]
    [InlineData("admin", "/api/titles/{titleId}/merge", "post", true)]
    [InlineData("admin", "/api/titles/{titleId}/enrich", "post", false)]
    [InlineData("admin", "/api/titles/{titleId}/artwork/{role}", "put", true)]
    [InlineData("admin", "/api/titles/{titleId}/artwork/{role}/apply", "post", false)]
    [InlineData("admin", "/api/titles/{titleId}/artwork-assets/{assetId}", "delete", true)]
    [InlineData("admin", "/api/titles/{titleId}/artwork-rejections/{assetId}", "put", true)]
    [InlineData("admin", "/api/titles/{titleId}/artwork-rejections/{assetId}", "delete", true)]
    [InlineData("admin", "/api/titles/{titleId}/hidden-artwork-candidates/{candidateId}", "put", true)]
    [InlineData("admin", "/api/titles/{titleId}/artwork-candidates/{candidateId}/import", "post", true)]
    [InlineData("admin", "/api/titles/{titleId}/provider-matches/{providerId}", "put", true)]
    [InlineData("admin", "/api/titles/{titleId}/provider-matches/{providerId}/confirm", "post", true)]
    [InlineData("admin", "/api/titles/{titleId}/provider-matches/{providerId}", "delete", true)]
    public void ConditionalWrite_DeclaresRequiredIfMatchAndPreconditionProblems_AndETagOnlyWithItsRepresentation(
        string host, string route, string method, bool returnsETag)
    {
        var operation = Operation(host, route, method);

        var ifMatch = operation.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "header"
                && parameter.GetProperty("name").GetString() == "If-Match")
            .ShouldHaveSingleItem();
        ifMatch.GetProperty("required").GetBoolean().ShouldBeTrue();
        foreach (string status in new[] { "400", "412", "428" })
        {
            operation.GetProperty("responses").GetProperty(status).GetProperty("content")
                .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString()
                .ShouldBe(ProblemDetailsReference);
        }

        string description = ifMatch.GetProperty("description").GetString().ShouldNotBeNull();
        description.ShouldContain("400");

        SuccessResponses(operation).ShouldAllBe(response => HasETag(response) == returnsETag);
    }

    [Theory]
    [InlineData("admin", "/api/systems/{systemKey}")]
    [InlineData("admin", "/api/systems/{systemKey}/overrides")]
    [InlineData("admin", "/api/companies/{companyKey}")]
    [InlineData("admin", "/api/companies/{companyKey}/overrides")]
    [InlineData("admin", "/api/regions/{regionKey}")]
    [InlineData("admin", "/api/languages/{languageKey}")]
    [InlineData("admin", "/api/catalog-snapshot")]
    [InlineData("consumer", "/api/systems/{systemKey}")]
    [InlineData("consumer", "/api/regions/{regionKey}")]
    [InlineData("consumer", "/api/rating-boards/{board}/ratings/{ratingCode}")]
    [InlineData("consumer", "/api/catalog-snapshot")]
    public void RevalidatingGet_DeclaresETagAndNotModified(string host, string route)
    {
        var operation = Operation(host, route, "get");

        operation.GetProperty("responses").TryGetProperty("304", out _).ShouldBeTrue();
        HasETag(operation.GetProperty("responses").GetProperty("200")).ShouldBeTrue();
        if (operation.TryGetProperty("parameters", out var parameters))
            parameters.EnumerateArray().ShouldNotContain(parameter => parameter.GetProperty("name").GetString() == "If-Match");
    }

    [Theory]
    [InlineData("admin", "/api/titles/{titleId}")]
    [InlineData("admin", "/api/titles/{titleId}/enrichment")]
    [InlineData("admin", "/api/titles/{titleId}/media")]
    [InlineData("admin", "/api/titles/{titleId}/media/{mediaId}")]
    [InlineData("admin", "/api/titles/{titleId}/artwork")]
    [InlineData("admin", "/api/titles/{titleId}/artwork/{role}")]
    [InlineData("admin", "/api/titles/{titleId}/artwork-assets")]
    [InlineData("admin", "/api/titles/{titleId}/artwork-rejections")]
    [InlineData("admin", "/api/titles/{titleId}/provider-matches/{providerId}")]
    public void FamilyValidatorGet_DeclaresETagWithoutNotModified(string host, string route)
    {
        var operation = Operation(host, route, "get");

        operation.GetProperty("responses").TryGetProperty("304", out _).ShouldBeFalse();
        HasETag(operation.GetProperty("responses").GetProperty("200")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/systems")]
    [InlineData("/api/companies")]
    public void Create_DeclaresTheCreatedResourcesETagWithoutAPrecondition(string route)
    {
        var operation = Operation("admin", route, "post");

        HasETag(operation.GetProperty("responses").GetProperty("201")).ShouldBeTrue();
        operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
            .ShouldNotContain(status => status == "412" || status == "428");
        if (operation.TryGetProperty("parameters", out var parameters))
            parameters.EnumerateArray().ShouldNotContain(parameter => parameter.GetProperty("name").GetString() == "If-Match");
    }

    // Region and language writes change no concurrency-controlled state: merge is a transition and aliases are
    // set membership guarded by uniqueness and existence.
    [Theory]
    [InlineData("/api/regions/{regionKey}/merge", "post")]
    [InlineData("/api/regions/{regionKey}/merge/preview", "post")]
    [InlineData("/api/regions/{regionKey}/aliases", "post")]
    [InlineData("/api/regions/{regionKey}/aliases/{aliasId}", "delete")]
    [InlineData("/api/languages/{languageKey}/merge", "post")]
    [InlineData("/api/languages/{languageKey}/merge/preview", "post")]
    [InlineData("/api/languages/{languageKey}/aliases", "post")]
    [InlineData("/api/languages/{languageKey}/aliases/{aliasId}", "delete")]
    public void TaxonomyWrite_DeclaresNoPrecondition(string route, string method)
    {
        var operation = Operation("admin", route, method);

        operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
            .ShouldNotContain(status => status == "412" || status == "428");
        if (operation.TryGetProperty("parameters", out var parameters))
            parameters.EnumerateArray().ShouldNotContain(parameter => parameter.GetProperty("name").GetString() == "If-Match");
    }

    [Theory]
    [InlineData("/api/titles/{titleId}/media")]
    [InlineData("/api/titles/{titleId}/artwork-candidates/{candidateId}/import")]
    public void MediaCreate_DeclaresTheCreated201AndTheRepeats200(string route)
    {
        var responses = Operation("admin", route, "post").GetProperty("responses");

        HasETag(responses.GetProperty("201")).ShouldBeTrue();
        HasETag(responses.GetProperty("200")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/titles/{titleId}/tracking", "put")]
    [InlineData("/api/titles/batch-set-tracking", "post")]
    [InlineData("/api/titles/{titleId}/games/{gameId}/move", "post")]
    [InlineData("/api/titles/{titleId}/enrichment-assessment", "get")]
    [InlineData("/api/titles/{titleId}/artwork-acquisition/fill", "post")]
    [InlineData("/api/titles/{titleId}/provider-matches", "get")]
    public void UnversionedTitleOperation_DeclaresNoPrecondition(string route, string method)
    {
        var operation = Operation("admin", route, method);

        operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
            .ShouldNotContain(status => status == "304" || status == "412" || status == "428");
        SuccessResponses(operation).ShouldAllBe(response => !HasETag(response));
        if (operation.TryGetProperty("parameters", out var parameters))
            parameters.EnumerateArray().ShouldNotContain(parameter => parameter.GetProperty("name").GetString() == "If-Match");
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("POST", true)]
    public async Task MismatchedUse_FailsWhenTheEndpointIsBuilt(string method, bool readUse)
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var route = app.MapMethods("/probe", [method], () => Results.Ok());
        _ = readUse ? route.ConditionalRead() : route.ConditionalWrite();

        Should.Throw<InvalidOperationException>(() =>
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConditionalWrite_DeclaresOneMalformedHeaderProblem_WithOrWithoutEndpointSpecificMetadata(
        bool declaresBefore, bool declaresAfter)
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var route = app.MapPatch("/probe", () => Results.Ok());
        if (declaresBefore)
            route.ProducesProblems(StatusCodes.Status400BadRequest);
        route.ConditionalWrite();
        if (declaresAfter)
            route.ProducesProblems(StatusCodes.Status400BadRequest);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ShouldHaveSingleItem();

        endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(metadata => metadata.StatusCode == StatusCodes.Status400BadRequest)
            .ShouldHaveSingleItem()
            .ContentTypes.ShouldBe(["application/problem+json"]);
    }

    private JsonElement Operation(string host, string route, string method) =>
        documents.Documents[host].RootElement.GetProperty("paths").GetProperty(route).GetProperty(method);

    private static IEnumerable<JsonElement> SuccessResponses(JsonElement operation) =>
        operation.GetProperty("responses").EnumerateObject()
            .Where(response => response.Name.StartsWith('2'))
            .Select(response => response.Value);

    private static bool HasETag(JsonElement response) =>
        response.TryGetProperty("headers", out var headers) && headers.TryGetProperty("ETag", out _);
}
