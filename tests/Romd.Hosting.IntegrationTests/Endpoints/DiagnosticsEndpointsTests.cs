using System.Net;
using System.Text.Json;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class DiagnosticsEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetOperationalDiagnostics_Admin_ReturnsBoundedOperationalSnapshot()
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/diagnostics/operational");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var diagnostics = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = diagnostics.RootElement;
        root.GetProperty("catalogProjections").GetProperty("items").GetArrayLength()
            .ShouldBeLessThanOrEqualTo(500);
        root.GetProperty("jobs").GetProperty("wedgedReplaceDatJobs").GetArrayLength()
            .ShouldBeLessThanOrEqualTo(100);
        root.GetProperty("jobs").GetProperty("strandedBulkEnrichmentJobs").GetArrayLength()
            .ShouldBeLessThanOrEqualTo(100);
        root.GetProperty("hangfire").GetProperty("queueBacklogs").EnumerateArray()
            .Select(queue => queue.GetProperty("queue").GetString())
            .ShouldBe(["default", "upload", "enrichment", "materialization"]);
        root.GetProperty("catalogProjections").GetProperty("items")[0]
            .GetProperty("state").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task GetOperationalDiagnostics_Anonymous_Returns401()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync("/api/diagnostics/operational");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOperationalDiagnostics_NonAdmin_Returns403()
    {
        using var client = fixture.CreateClient()
            .WithTestUser(Guid.NewGuid(), "manager@localhost", [RomdRoleType.Manager]);

        using var response = await client.GetAsync("/api/diagnostics/operational");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
