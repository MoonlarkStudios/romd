using System.Text.Json;
using Romd.Admin.Application.Ingestion.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class S5cJobOpenApiTests(ApiContractPolicyGuardrailTests.LiveOpenApiDocuments documents)
    : IClassFixture<ApiContractPolicyGuardrailTests.LiveOpenApiDocuments>
{
    [Fact]
    public void CanonicalJobCollectionAndTypedFeeds_PublishCommonPagesClosedFiltersAndLimits()
    {
        var paths = documents.Documents["admin"].RootElement.GetProperty("paths");
        var schemas = documents.Documents["admin"].RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var (path, itemSchema) in new[] { ("/api/jobs", "Job"), ("/api/jobs/{jobId}/items", "JobItem"),
            ("/api/jobs/{jobId}/artwork-items", "ArtworkCompletionItem") })
        {
            var get = paths.GetProperty(path).GetProperty("get");
            get.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString().ShouldBe($"#/components/schemas/PageOf{itemSchema}");
            schemas.GetProperty("PageOf" + itemSchema).GetProperty("properties").EnumerateObject().Select(field => field.Name).Order()
                .ShouldBe(["hasNextPage", "items", "nextCursor"]);
            var limit = Parameter(get, "limit").GetProperty("schema");
            limit.GetProperty("minimum").GetInt32().ShouldBe(1);
            limit.GetProperty("maximum").GetInt32().ShouldBe(100);
        }
        schemas.GetProperty("Job").GetProperty("discriminator").GetProperty("mapping").EnumerateObject().Select(member => member.Name).Order()
            .ShouldBe(JobFilters.Types.Order());
        schemas.GetProperty("JobMediaReconciliationJob").GetProperty("properties")
            .TryGetProperty("selection", out _).ShouldBeTrue();
        schemas.GetProperty("MediaReconciliationSubject").GetProperty("properties")
            .TryGetProperty("id", out _).ShouldBeTrue();
        var list = paths.GetProperty("/api/jobs").GetProperty("get");
        Parameter(list, "archive").GetProperty("schema").GetProperty("default").GetString().ShouldBe("active");
        Parameter(list, "jobType").GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ShouldBe(JobFilters.Types);
        Parameter(list, "outcome").GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ShouldBe(JobFilters.Outcomes);
        Parameter(list, "correlationId").GetProperty("schema").GetProperty("format").GetString().ShouldBe("uuid");
        Parameter(list, "query").GetProperty("schema").GetProperty("maxLength").GetInt32().ShouldBe(200);
        AssertValues(list, "archive", JobFilters.Archives);
        AssertValues(paths.GetProperty("/api/jobs/{jobId}/items").GetProperty("get"), "outcome", JobFilters.ItemOutcomes);
        AssertValues(paths.GetProperty("/api/jobs/{jobId}/artwork-items").GetProperty("get"), "status", JobFilters.ArtworkStatuses);
        AssertValues(paths.GetProperty("/api/jobs/{jobId}/item-export").GetProperty("get"), "format", JobFilters.ExportFormats);
        var update = paths.GetProperty("/api/jobs/{jobId}").GetProperty("patch");
        update.GetProperty("responses").TryGetProperty("200", out _).ShouldBeTrue();
        schemas.GetProperty("UpdateJobRequest").GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(["isArchived"]);
    }
    [Fact]
    public void RetiredJobReadAndTicketRoutes_HaveNoOpenApiOperations()
    {
        var paths = documents.Documents["admin"].RootElement.GetProperty("paths");
        foreach (string path in new[] { "/api/jobs/{jobId}/verification-items", "/api/media/convergence", "/api/media/chd/reverification",
            "/api/jobs/history", "/api/jobs/{id}", "/api/jobs/{jobId}/archive", "/api/jobs/{id}/archive",
            "/api/jobs/{jobId}/items/export", "/api/upload/batches/{batchId}", "/api/artwork/completion/{jobId}",
            "/api/artwork/completion/{jobId}/items", "/api/admin/media/chd/reverification/{jobId}/items",
            "/api/export/{jobId}/download", "/api/export/{jobId}/download-ticket", "/api/jobs/{jobId}/download-ticket" })
            paths.TryGetProperty(path, out _).ShouldBeFalse(path);
        paths.GetProperty("/api/artwork/completion").TryGetProperty("get", out _).ShouldBeFalse();
        var schemas = documents.Documents["admin"].RootElement.GetProperty("components").GetProperty("schemas");
        foreach (string schema in new[] { "JobHistoryPage", "JobItemPage", "ArtworkCompletionItems", "ChdReverificationItemsDto", "ChdReverificationItemDto", "PageOfChdReverificationItemDto" })
            schemas.TryGetProperty(schema, out _).ShouldBeFalse(schema);
    }
    private static void AssertValues(JsonElement operation, string name, IReadOnlyList<string> values) =>
        Parameter(operation, name).GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ShouldBe(values);
    private static JsonElement Parameter(JsonElement operation, string name) => operation.GetProperty("parameters").EnumerateArray()
        .Single(parameter => parameter.GetProperty("name").GetString() == name);
}
