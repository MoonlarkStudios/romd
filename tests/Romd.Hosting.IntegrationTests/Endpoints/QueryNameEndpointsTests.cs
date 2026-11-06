using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Closed query names on the real admin host (docs/decisions/api-contract-policy.md, "Strict Validation"): a
///     misspelled filter, a repeated scalar or a differently cased name is refused, keyed by the name sent, instead of
///     widening the result; documented arrays repeat; the refusal follows authorization and precedes the body.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class QueryNameEndpointsTests(IntegrationTestFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.CreateAuthenticatedClient();

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("/api/me?x=1", "x")]
    [InlineData("/api/jobs?jobTyp=Upload", "jobTyp")]
    [InlineData("/api/jobs?limit=1&limit=2", "limit")]
    [InlineData("/api/jobs?Limit=1", "Limit")]
    [InlineData("/api/catalog?system=snes", "system")]
    [InlineData("/api/catalog?query=a&query=b", "query")]
    [InlineData("/api/catalog?Enrichment=NeedsReview&enrichment=Blocked", "Enrichment")]
    [InlineData("/api/catalog?enrichment=NeedsReview&Enrichment=Blocked", "Enrichment")]
    [InlineData("/api/jobs?bogus=1&limit=abc", "bogus")]
    public async Task UndeclaredRepeatedOrRecasedName_Is400KeyedByTheNameSent(string path, string field)
    {
        using var response = await _client.GetAsync(path);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        Fields(body).ShouldBe([field]);
    }

    [Fact]
    public async Task DocumentedArrayFilter_Repeats()
    {
        using var response = await _client.GetAsync("/api/catalog?enrichment=NeedsReview&enrichment=Blocked&limit=1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refusal_FollowsAuthorizationAndPrecedesAMalformedBody()
    {
        using var user = fixture.CreateClient().WithTestUser(Guid.NewGuid(), "reader@localhost", [RomdRoleType.User]);
        using var forbidden = await user.PostAsJsonAsync("/api/regions/asia/aliases?bogus=1", new { alias = "reader" });
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var malformed = await _client.PostAsync("/api/regions/asia/aliases?bogus=1",
            new StringContent("{", Encoding.UTF8, "application/json"));
        using var body = await ProblemEnvelope.ReadAsync(malformed, 400, "Request.InvalidParameter");
        Fields(body).ShouldBe(["bogus"]);
    }

    [Fact]
    public async Task Upload_WithAMisspelledOption_IsRefusedWithoutCreatingAJob()
    {
        int before = await JobCountAsync();
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.ASCII.GetBytes("not a rom")), "file", "game.bin" }
        };

        using var response = await _client.PostAsync("/api/upload?maxParalelRoms=2", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        Fields(body).ShouldBe(["maxParalelRoms"]);
        (await JobCountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task ProtocolSurfaces_KeepTheirOwnQueries()
    {
        using var health = await _client.GetAsync("/health?probe=1");

        health.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<int> JobCountAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Jobs.CountAsync();
    }

    private static string[] Fields(JsonDocument body) =>
        body.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ToArray();
}
