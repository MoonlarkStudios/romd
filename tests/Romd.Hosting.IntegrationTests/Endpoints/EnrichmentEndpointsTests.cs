using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for the named-string <c>EnrichmentScope</c> query parameter
///     (docs/decisions/api-contract-policy.md, "Opaque Public Identity" and
///     "Strict Validation"): the admin SPA sends named scope strings, and an unknown scope fails
///     with the shared 400 envelope instead of binding silently.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class EnrichmentEndpointsTests : IDisposable
{
    private readonly HttpClient _client;

    public EnrichmentEndpointsTests(IntegrationTestFixture fixture) =>
        _client = fixture.CreateAuthenticatedClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task MetadataPolicy_ValidPayloadWithoutIfMatch_Returns428()
    {
        string systemKey = await TestHelpers.GetFirstSystemKeyAsync(_client);
        string path = $"/api/systems/{systemKey}/enrichment/defaults";
        using var read = await _client.GetAsync(path);
        read.EnsureSuccessStatusCode();
        using var baseline = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        using var response = await _client.PatchAsJsonAsync(path, new
        {
            defaults = new Dictionary<string, string?>()
        });
        using var problem = await ProblemEnvelope.ReadAsync(response, 428, "MetadataPolicy.PreconditionRequired");
    }

    [Theory]
    [InlineData("*", 428, "MetadataPolicy.PreconditionRequired")]
    [InlineData("bad-header", 400, "Request.InvalidParameter")]
    [InlineData("W/\"weak\"", 412, "MetadataPolicy.PreconditionFailed")]
    [InlineData("\"stale\"", 412, "MetadataPolicy.PreconditionFailed")]
    public async Task MetadataPolicy_InvalidPrecondition_UsesSharedSemantics(string tag, int status, string errorCode)
    {
        string key = await TestHelpers.GetFirstSystemKeyAsync(_client);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/{key}/enrichment/defaults")
        { Content = JsonContent.Create(new { defaults = new Dictionary<string, string?>() }) };
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        using var response = await _client.SendAsync(request);
        using var problem = await ProblemEnvelope.ReadAsync(response, status, errorCode);
        if (status == 400)
            problem.RootElement.GetProperty("errors").TryGetProperty("If-Match", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task MetadataPolicy_ChangedPatch_ReturnsRepresentationTagAndJob_StaleWriteFails_ConditionalReadStill200()
    {
        string key = await TestHelpers.GetFirstSystemKeyAsync(_client);
        string path = $"/api/systems/{key}/enrichment/defaults";
        using var before = await _client.GetAsync(path);
        before.EnsureSuccessStatusCode();
        string tag = before.Headers.ETag!.ToString();
        using var baseline = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        string? current = baseline.RootElement.GetProperty("defaults").TryGetProperty("Description", out var existing) ? existing.GetString() : null;
        using var patch = new HttpRequestMessage(HttpMethod.Patch, path)
        { Content = JsonContent.Create(new { defaults = new Dictionary<string, string?> { ["Description"] = current == "user" ? null : "user" } }) };
        patch.Headers.TryAddWithoutValidation("If-Match", tag);
        using var response = await _client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string updatedTag = response.Headers.ETag!.ToString();
        updatedTag.ShouldNotBe(tag);
        updatedTag.ShouldBe("\"" + saved.RootElement.GetProperty("revision").GetString() + "\"");
        Guid job = saved.RootElement.GetProperty("pendingJobId").GetGuid();
        using var jobRead = await _client.GetAsync($"/api/jobs/{job}");
        jobRead.EnsureSuccessStatusCode();
        using var jobBody = JsonDocument.Parse(await jobRead.Content.ReadAsStringAsync());
        jobBody.RootElement.GetProperty("jobType").GetString().ShouldBe("metadata-rematerialization");
        jobBody.RootElement.GetProperty("systemKey").GetString().ShouldBe(key);
        using var read = new HttpRequestMessage(HttpMethod.Get, path);
        read.Headers.TryAddWithoutValidation("If-None-Match", updatedTag);
        using var after = await _client.SendAsync(read);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.Headers.ETag!.ToString().ShouldBe(updatedTag);
        using var cancel = await _client.PostAsync($"/api/jobs/{job}/cancel", null);
        using var cancelProblem = await ProblemEnvelope.ReadAsync(cancel, 409, "Jobs.NotCancellable");
        using var retry = await _client.PostAsync($"/api/jobs/{job}/retry-failed", null);
        using var retryProblem = await ProblemEnvelope.ReadAsync(retry, 409, "MetadataRematerialization.RetryUnsupported");
        using var stale = new HttpRequestMessage(HttpMethod.Patch, path)
        { Content = JsonContent.Create(new { defaults = new Dictionary<string, string?>() }) };
        stale.Headers.TryAddWithoutValidation("If-Match", tag);
        using var staleResponse = await _client.SendAsync(stale);
        using var problem = await ProblemEnvelope.ReadAsync(staleResponse, 412, "MetadataPolicy.PreconditionFailed");
    }

    [Fact]
    public async Task TriggerBulkEnrichment_NamedScopeString_Returns202()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.PostAsync(
            $"/api/systems/{platformId}/enrichment/bulk?scope=Tracked",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task TriggerBulkEnrichment_AllScopeString_Returns202()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.PostAsync(
            $"/api/systems/{platformId}/enrichment/bulk?scope=All",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("5")]
    public async Task TriggerBulkEnrichment_NumericScope_Returns400Envelope(string numericScope)
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.PostAsync(
            $"/api/systems/{platformId}/enrichment/bulk?scope={numericScope}",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidParameter");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        // Keyed by the parameter; like every binding failure, the message never echoes the value.
        root.GetProperty("errors").GetProperty("scope").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task TriggerBulkEnrichment_UnknownScope_Returns400Envelope()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.PostAsync(
            $"/api/systems/{platformId}/enrichment/bulk?scope=Bogus",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidParameter");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
