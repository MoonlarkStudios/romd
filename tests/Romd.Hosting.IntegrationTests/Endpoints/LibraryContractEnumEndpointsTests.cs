using System.Net;
using System.Text;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for library policy enums: every value crosses the admin boundary as a
///     named string, while integer values and unknown names fail with the shared 400 envelope.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class LibraryContractEnumEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task CreateLibrary_NamedPolicyEnums_MapAndSerializeAsNamedStrings()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string name = $"Named policy enums {Guid.NewGuid():N}";
        string body = $$"""
            {
              "name": "{{name}}",
              "configuration": {
                "titleSelectionMode": "IncludeOnly",
                "contentRatingPolicy": {
                  "basisSelection": "Preferred",
                  "boardPreference": ["Pegi", "Esrb"],
                  "maxMinimumAge": 16,
                  "unknownRatingPolicy": "Hide"
                },
                "unknownGenrePolicy": "NeedsReview"
              }
            }
            """;

        using var response = await client.PostAsync(
            "/api/libraries",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var configuration = document.RootElement.GetProperty("configuration");
        configuration.GetProperty("titleSelectionMode").GetString().ShouldBe("IncludeOnly");
        configuration.GetProperty("unknownGenrePolicy").GetString().ShouldBe("NeedsReview");

        var policy = configuration.GetProperty("contentRatingPolicy");
        policy.GetProperty("basisSelection").GetString().ShouldBe("Preferred");
        policy.GetProperty("unknownRatingPolicy").GetString().ShouldBe("Hide");
        policy.GetProperty("boardPreference")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ShouldBe(["Pegi", "Esrb"]);
    }

    [Theory]
    [InlineData("{\"titleSelectionMode\":0}")]
    [InlineData("{\"titleSelectionMode\":\"NotAMode\"}")]
    [InlineData("{\"titleSelectionMode\":\"includeonly\"}")]
    [InlineData("{\"titleSelectionMode\":\"Rules, IncludeOnly\"}")]
    [InlineData("{\"unknownGenrePolicy\":0}")]
    [InlineData("{\"unknownGenrePolicy\":\"NotAPolicy\"}")]
    [InlineData("{\"unknownGenrePolicy\":\"Allow, NeedsReview\"}")]
    [InlineData("{\"contentRatingPolicy\":{\"basisSelection\":0}}")]
    [InlineData("{\"contentRatingPolicy\":{\"basisSelection\":\"NotABasis\"}}")]
    [InlineData("{\"contentRatingPolicy\":{\"boardPreference\":[0]}}")]
    [InlineData("{\"contentRatingPolicy\":{\"boardPreference\":[\"NotABoard\"]}}")]
    [InlineData("{\"contentRatingPolicy\":{\"unknownRatingPolicy\":0}}")]
    [InlineData("{\"contentRatingPolicy\":{\"unknownRatingPolicy\":\"NotAPolicy\"}}")]
    public async Task CreateLibrary_InvalidPolicyEnum_Returns400Envelope(string configurationJson)
    {
        using var client = fixture.CreateAuthenticatedClient();
        string name = $"Invalid enum {Guid.NewGuid():N}";
        string body = $$"""{"name":"{{name}}","configuration":{{configurationJson}}}""";

        using var response = await client.PostAsync(
            "/api/libraries",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidBody");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateLibrary_QuotedNumericPolicyValue_Returns400Envelope()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string body = $$"""
            {
              "name": "Quoted numeric {{Guid.NewGuid():N}}",
              "configuration": {
                "contentRatingPolicy": { "maxMinimumAge": "16" }
              }
            }
            """;

        using var response = await client.PostAsync(
            "/api/libraries",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidBody");
    }
}
