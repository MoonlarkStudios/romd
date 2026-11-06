using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class JobAcceptedOpenApiContractTests(ApiContractPolicyGuardrailTests.LiveOpenApiDocuments documents)
    : IClassFixture<ApiContractPolicyGuardrailTests.LiveOpenApiDocuments>
{
    [Fact]
    public void EveryBackgroundAcceptance_DeclaresCanonicalJobReferenceLocationAndNoETag()
    {
        var operations = documents.Operations.Where(operation => operation.Documents("202")).ToArray();
        operations.Length.ShouldBe(17);
        foreach (var operation in operations)
        {
            var accepted = operation.Definition.GetProperty("responses").GetProperty("202");
            accepted.GetProperty("content").GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/JobReference", operation.Key);
            var headers = accepted.GetProperty("headers");
            headers.GetProperty("Location").GetProperty("required").GetBoolean().ShouldBeTrue(operation.Key);
            headers.GetProperty("Location").GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("/api/jobs/{jobId}");
            headers.EnumerateObject().ShouldNotContain(header => header.Name.Equals("ETag", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ReplacementMultipart_DeclaresRequiredSnapshotPinsAndSameChangeConstraintsAsSubscription()
    {
        var paths = documents.Documents["admin"].RootElement.GetProperty("paths");
        var apply = paths.GetProperty("/api/dats/{datId}/replacements").GetProperty("post");
        apply.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("snapshot binding");
        var schema = apply.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ShouldContain("activeSha256");
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ShouldContain("candidateSha256");
        var changes = paths.GetProperty("/api/dats/{datId}/replacements/changes").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema")
            .GetProperty("allOf").EnumerateArray().SelectMany(part => part.GetProperty("properties").EnumerateObject())
            .ToDictionary(property => property.Name, property => property.Value);
        changes["activeSha256"].GetProperty("pattern").GetString().ShouldBe(Romd.Admin.Application.Source.Dat.DatReviewPins.ActivePattern);
        changes["candidateSha256"].GetProperty("minLength").GetInt32().ShouldBe(64);
        changes["change"].GetProperty("pattern").GetString().ShouldBe(Romd.Admin.Application.Source.Dat.DatChangePageRequest.ChangePattern);
        changes["query"].GetProperty("maxLength").GetInt32().ShouldBe(200);
    }
}
