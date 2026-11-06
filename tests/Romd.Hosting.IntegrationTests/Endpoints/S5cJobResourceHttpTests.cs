using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Romd.Host.Authorization;
using Romd.Hosting.IntegrationTests.Hosting;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class S5cJobResourceHttpTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task List_Unfiltered_ReturnsCanonicalPage()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/jobs");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        body.RootElement.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);
        body.RootElement.GetProperty("hasNextPage").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
    }
    [Theory]
    [InlineData("outcome=not-real")]
    [InlineData("jobType=not-real")]
    [InlineData("archive=not-real")]
    public async Task List_UnknownClosedFilter_ReturnsAttributed400(string query)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/jobs?" + query);
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").TryGetProperty(query.Split('=')[0], out _).ShouldBeTrue();
    }
    [Fact]
    public async Task List_MalformedOpaqueCursor_Returns400InsteadOfSilentlyIgnoringIt()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/jobs?cursor=not-a-cursor");
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Jobs.InvalidCursor");
    }
    [Fact]
    public async Task Detail_MalformedId_ReturnsBinding400()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/jobs/not-a-guid");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }
    [Fact]
    public async Task Detail_UnknownId_ReturnsJobsEnvelope()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}");
        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Jobs.NotFound");
    }
    [Fact]
    public async Task Update_TerminalJob_ArchivesAndUnarchivesWithActualRepresentation()
    {
        var job = UploadJob.Create("s5c-archive.zip", createdAt: DateTimeOffset.UtcNow);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        job.Cancel(cancelledAt: DateTimeOffset.UtcNow);
        await SeedAsync(UploadJobEntity.FromDomain(job));
        using var client = fixture.CreateAuthenticatedClient();
        foreach (bool archived in new[] { true, true, false })
        {
            using var response = await client.PatchAsJsonAsync($"/api/jobs/{job.Id}", new { isArchived = archived });
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("id").GetGuid().ShouldBe(job.Id);
            body.RootElement.GetProperty("isArchived").GetBoolean().ShouldBe(archived);
        }
    }
    [Fact]
    public async Task Items_IncompatibleFamily_ReturnsFeedUnavailable()
    {
        var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, null);
        job.Fail("Read-only wrong-family fixture.", failedAt: DateTimeOffset.UtcNow);
        await SeedAsync(ExportJobEntity.FromDomain(job));
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/jobs/{job.Id}/items");
        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Jobs.FeedUnavailable");
    }
    [Fact]
    public async Task Items_UnknownOutcome_ReturnsAttributed400()
    {
        var job = UploadJob.Create("s5c-items.zip", createdAt: DateTimeOffset.UtcNow);
        job.Fail("Read-only filter fixture.", failedAt: DateTimeOffset.UtcNow);
        await SeedAsync(UploadJobEntity.FromDomain(job));
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/jobs/{job.Id}/items?outcome=not-real");
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
    }
    [Fact]
    public async Task ArtworkSummary_CompletionJob_ExposesProjectionAtCanonicalSubresource()
    {
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 0, null, createdAt: DateTimeOffset.UtcNow, "Artwork");
        job.Fail("Read-only summary fixture.", failedAt: DateTimeOffset.UtcNow);
        await SeedAsync(ArtworkCompletionJobEntity.FromDomain(job));
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/jobs/{job.Id}/artwork-summary");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("roles").ValueKind.ShouldBe(JsonValueKind.Array);
        body.RootElement.GetProperty("unfinished").GetInt32().ShouldBe(0);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isArchived\":true,\"unknown\":true}")]
    [InlineData("{\"isArchived\":\"true\"}")]
    public async Task Update_MissingUnknownOrWrongTypedField_Is400(string body)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PatchAsync($"/api/jobs/{Guid.NewGuid()}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody");
    }
    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("outcome=1")]
    [InlineData("jobType=Upload")]
    [InlineData("archive=ALL")]
    public async Task List_InvalidRangeOrCase_IsAttributed400(string query)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/jobs?" + query);
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").TryGetProperty(query.Split('=')[0], out _).ShouldBeTrue();
    }
    [Fact]
    public async Task Mutations_StateConflictsAndRetainedDelete501_AreDocumentedEnvelopes()
    {
        var active = UploadJob.Create("active.zip", createdAt: DateTimeOffset.UtcNow);
        active.Start("s5c-state-fixture", startedAt: DateTimeOffset.UtcNow);
        await SeedAsync(UploadJobEntity.FromDomain(active));
        var terminal = UploadJob.Create("terminal.zip", createdAt: DateTimeOffset.UtcNow);
        terminal.Fail("done", failedAt: DateTimeOffset.UtcNow);
        await SeedAsync(UploadJobEntity.FromDomain(terminal));
        using var client = fixture.CreateAuthenticatedClient();
        using (var response = await client.PatchAsJsonAsync($"/api/jobs/{active.Id}", new { isArchived = true }))
        using (await ProblemEnvelope.ReadAsync(response, 409, "Jobs.NotTerminal"))
        { }
        using (var response = await client.PostAsync($"/api/jobs/{terminal.Id}/cancel", null))
        using (await ProblemEnvelope.ReadAsync(response, 409, "Jobs.AlreadyComplete"))
        { }
        using (var response = await client.DeleteAsync($"/api/jobs/{terminal.Id}"))
        using (await ProblemEnvelope.ReadAsync(response, 409, "Jobs.NotArchived"))
        { }
        using (var archived = await client.PatchAsJsonAsync($"/api/jobs/{terminal.Id}", new { isArchived = true }))
            archived.EnsureSuccessStatusCode();
        using (var response = await client.DeleteAsync($"/api/jobs/{terminal.Id}"))
        using (await ProblemEnvelope.ReadAsync(response, 501, "Jobs.DeleteNotImplemented"))
        { }
    }

    [Fact]
    public void RuntimeRoutes_UseCanonicalIdentityTypedRolesAndRetireSiblingReads()
    {
        var endpoints = AuthorizationInventory.GetRouteEndpoints(fixture.Services);
        foreach (var (path, policy) in new[] { ("/api/jobs/{jobId}/items", AuthorizationPolicies.RequireUser),
            ("/api/jobs/{jobId}/artwork-items", AuthorizationPolicies.RequireManager),
            ("/api/jobs/{jobId}/artwork-summary", AuthorizationPolicies.RequireManager),
            ("/api/media/reconciliation/policies", AuthorizationPolicies.RequireAdmin) })
            endpoints.Single(endpoint => endpoint.RoutePattern.RawText == path).Metadata.GetOrderedMetadata<IAuthorizeData>()
                .ShouldContain(data => data.Policy == policy);
        foreach (string path in new[] { "/api/jobs/{jobId}/verification-items", "/api/media/convergence", "/api/media/chd/reverification",
            "/api/jobs/history", "/api/jobs/{id:guid}", "/api/jobs/{id:guid}/archive",
            "/api/jobs/{id:guid}/items/export", "/api/upload/batches/{batchId:guid}", "/api/artwork/completion/{jobId:guid}",
            "/api/artwork/completion/{jobId:guid}/items", "/api/admin/media/chd/reverification/{jobId:guid}/items",
            "/api/export/{jobId:guid}/download", "/api/export/{jobId:guid}/download-ticket", "/api/jobs/{jobId}/download-ticket" })
            endpoints.ShouldNotContain(endpoint => endpoint.RoutePattern.RawText == path);
        endpoints.Where(endpoint => endpoint.RoutePattern.RawText == "/api/artwork/completion")
            .ShouldNotContain(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
    }

    private async Task SeedAsync(JobEntity job)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        db.Add(job);
        await db.SaveChangesAsync();
    }
}
