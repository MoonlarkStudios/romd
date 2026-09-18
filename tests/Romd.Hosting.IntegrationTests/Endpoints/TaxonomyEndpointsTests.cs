using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Romd.Application.Common.Ids;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for Sqid-typed taxonomy alias identity
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity" and
///     "Strict Validation"): region and language alias ids are Sqid strings in list responses
///     and delete routes, and a malformed alias Sqid fails with the shared 400 envelope.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class TaxonomyEndpointsTests : IDisposable
{
    private readonly HttpClient _client;

    public TaxonomyEndpointsTests(IntegrationTestFixture fixture) =>
        _client = fixture.CreateAuthenticatedClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task RegionAlias_AddListDeleteRoundTrip_UsesSqidIds()
    {
        string regionId = await GetFirstEntryIdAsync("/api/taxonomy/regions");
        string aliasText = $"region-alias-{Guid.NewGuid():N}";

        var addResponse = await _client.PostAsJsonAsync(
            $"/api/taxonomy/regions/{regionId}/aliases",
            new { alias = aliasText });
        addResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        string aliasId = await FindAliasIdAsync("/api/taxonomy/regions", regionId, aliasText);
        IdCoder.TryDecode(aliasId, out _).ShouldBeTrue($"'{aliasId}' should be a Sqid");

        var deleteResponse = await _client.DeleteAsync(
            $"/api/taxonomy/regions/{regionId}/aliases/{aliasId}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task LanguageAlias_AddListDeleteRoundTrip_UsesSqidIds()
    {
        string languageId = await GetFirstEntryIdAsync("/api/taxonomy/languages");
        string aliasText = $"language-alias-{Guid.NewGuid():N}";

        var addResponse = await _client.PostAsJsonAsync(
            $"/api/taxonomy/languages/{languageId}/aliases",
            new { alias = aliasText });
        addResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        string aliasId = await FindAliasIdAsync("/api/taxonomy/languages", languageId, aliasText);
        IdCoder.TryDecode(aliasId, out _).ShouldBeTrue($"'{aliasId}' should be a Sqid");

        var deleteResponse = await _client.DeleteAsync(
            $"/api/taxonomy/languages/{languageId}/aliases/{aliasId}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemoveRegionAlias_MalformedAliasSqid_Returns400Envelope()
    {
        string regionId = await GetFirstEntryIdAsync("/api/taxonomy/regions");

        var response = await _client.DeleteAsync(
            $"/api/taxonomy/regions/{regionId}/aliases/not-a-sqid");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidBody");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>Returns the id of the first seeded taxonomy entry at the given list URL.</summary>
    private async Task<string> GetFirstEntryIdAsync(string listUrl)
    {
        var response = await _client.GetAsync(listUrl);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetArrayLength().ShouldBeGreaterThan(0, $"{listUrl} should be seeded");

        return doc.RootElement[0].GetProperty("id").GetString().ShouldNotBeNull();
    }

    /// <summary>Finds the alias id for the given alias text on the given taxonomy entry.</summary>
    private async Task<string> FindAliasIdAsync(string listUrl, string entryId, string aliasText)
    {
        var response = await _client.GetAsync(listUrl);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == entryId);
        var alias = entry.GetProperty("aliases").EnumerateArray()
            .Single(a => a.GetProperty("alias").GetString() == aliasText);

        return alias.GetProperty("id").GetString().ShouldNotBeNull();
    }
}
