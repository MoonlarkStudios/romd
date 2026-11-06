using System.Net.Http.Json;
using Romd.Application.Common.Ids;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     The one download-ticket collection (docs/decisions/api-contract-policy.md, "Resource Model";
///     docs/decisions/api-contract-policy.md, "Strict Validation"): the body names a title, release or file. The
///     target is a body enum, so an unknown one fails binding; a malformed id or an unknown format is a 400 keyed by
///     its field, and an unresolved resource is 404.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class DownloadTicketEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Create_MalformedIdAndUnknownFormat_IsOne400KeyedByEachField()
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/download-tickets",
            new { target = "Title", id = "not-an-id", format = "iso" });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Export.InvalidDownloadTicket");
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order()
            .ShouldBe(["format", "id"]);
    }

    [Theory]
    [InlineData("\"title\"")]
    [InlineData("\"Roms\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    public async Task Create_UnknownTarget_IsTheBodyProblem(string target)
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsync("/api/download-tickets", new StringContent(
            $$"""{"target":{{target}},"id":"{{IdCoder.Encode(1)}}"}""", System.Text.Encoding.UTF8, "application/json"));

        using (await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody"))
        { }
    }

    [Fact]
    public async Task Create_WithoutTargetOrId_IsTheBodyProblem()
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/download-tickets", new { format = "original" });

        using (await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody"))
        { }
    }

    [Theory]
    [InlineData("Title", "Export.NotFound")]
    [InlineData("Release", "Export.ReleaseUnavailable")]
    [InlineData("Rom", "Export.FileNotFound")]
    public async Task Create_UnresolvedResource_Is404(string target, string errorCode)
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/download-tickets",
            new { target, id = IdCoder.Encode(int.MaxValue) });

        using (await ProblemEnvelope.ReadAsync(response, 404, errorCode))
        { }
    }

    [Fact]
    public async Task Create_Anonymous_IsUnauthorized()
    {
        using var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/download-tickets",
            new { target = "Rom", id = IdCoder.Encode(1) });

        ((int)response.StatusCode).ShouldBe(401);
    }
}
