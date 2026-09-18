using System.Net;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for the named-string <c>EnrichmentScope</c> query parameter
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity" and
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
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidBody");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errors").GetProperty("scope")[0].GetString()
            .ShouldNotBeNull().ShouldContain($"'{numericScope}'");
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
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidBody");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
