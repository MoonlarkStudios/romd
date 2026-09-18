using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Romd.Application.Common.Ids;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for Sqid-typed platform alias identity
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity" and
///     "Strict Validation"): alias ids are Sqid strings in responses, Location headers, and
///     delete routes, and a malformed alias Sqid fails with the shared 400 envelope.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PlatformEndpointsTests : IDisposable
{
    private readonly HttpClient _client;

    public PlatformEndpointsTests(IntegrationTestFixture fixture) =>
        _client = fixture.CreateAuthenticatedClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task AddAlias_ReturnsSqidIdAndSqidLocation()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.PostAsJsonAsync(
            $"/api/systems/{platformId}/aliases",
            new { type = "name", value = $"Alias {Guid.NewGuid():N}" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string aliasId = doc.RootElement.GetProperty("id").GetString().ShouldNotBeNull();
        IdCoder.TryDecode(aliasId, out _).ShouldBeTrue($"'{aliasId}' should be a Sqid");
        response.Headers.Location.ShouldNotBeNull()
            .OriginalString.ShouldBe($"/api/systems/{platformId}/aliases/{aliasId}");
    }

    [Fact]
    public async Task RemoveAlias_ViaReturnedSqidId_Returns204()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);
        var createResponse = await _client.PostAsJsonAsync(
            $"/api/systems/{platformId}/aliases",
            new { type = "name", value = $"Alias {Guid.NewGuid():N}" });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        string aliasId = created.RootElement.GetProperty("id").GetString().ShouldNotBeNull();

        var deleteResponse = await _client.DeleteAsync($"/api/systems/{platformId}/aliases/{aliasId}");

        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemoveAlias_MalformedAliasSqid_Returns400Envelope()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.DeleteAsync($"/api/systems/{platformId}/aliases/not-a-sqid");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidBody");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
