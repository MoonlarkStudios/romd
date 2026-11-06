using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for the named-string rating enums
///     (docs/decisions/api-contract-policy.md, "Opaque Public Identity"): the published snapshot's rating
///     boards and categories match what a title's rating routes accept, title content ratings serialize
///     boards/designations as named strings, and a title's rating routes accept only named board keys.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ContentRatingEndpointsTests : IDisposable
{
    private static readonly string[] ExpectedBoardNames =
        ["Esrb", "Pegi", "Cero", "Usk", "Grac", "ClassInd", "Acb"];

    private static readonly string[] ValidDesignationNames =
        ["Rated", "RatingPending", "RefusedClassification"];

    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public ContentRatingEndpointsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateAuthenticatedClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CatalogSnapshotRatings_MatchTheBoardsAndCategoriesTheTitleWriteAccepts()
    {
        // The re-rate picker reads the snapshot and narrows its strings to the contract enums, and the title
        // write validates codes against the domain catalog, so the two must agree exactly.
        var snapshot = (await _client.ReadCatalogAsync());

        snapshot.RatingBoards.Select(board => board.Key).ShouldBe(ExpectedBoardNames, ignoreOrder: true);
        Enum.GetNames<Models.RatingBoard>().ShouldBe(ExpectedBoardNames, ignoreOrder: true);
        Enum.GetNames<Models.RatingDesignation>().ShouldBe(ValidDesignationNames, ignoreOrder: true);
        foreach (var board in Enum.GetValues<RatingBoard>())
        {
            var published = snapshot.Ratings
                .Where(rating => rating.Board == board.ToString() && !rating.Retired)
                .Select(rating => (rating.Code, rating.Designation, rating.MinimumAge))
                .OrderBy(rating => rating.MinimumAge ?? int.MaxValue).ThenBy(rating => rating.Code, StringComparer.Ordinal);
            var accepted = RatingBoardCatalog.GetCategories(board)
                .Select(category => (category.Code, (string?)category.Designation.ToString(), category.MinimumAge));
            published.ShouldBe(accepted, board.ToString());
        }
    }

    [Fact]
    public async Task GetRatingBoardCatalog_RouteIsDeleted()
    {
        using var response = await _client.GetAsync("/api/catalog/rating-boards");

        using (await ProblemEnvelope.ReadAsync(response, 404, "Request.RouteNotFound"))
        { }
    }

    [Fact]
    public async Task SetContentRating_NamedBoardRoute_PersistsAndSerializesNamedStrings()
    {
        string titleId = await SeedTitleAsync();
        var etag = await ETagAsync(titleId);

        using var response = await _client.SendAsync(Rating(HttpMethod.Put, titleId, "Esrb", etag, new { code = "E10+" }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.ETag!.Tag.ShouldNotBe(etag);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rating = doc.RootElement.GetProperty("contentRatings").EnumerateArray()
            .Single(r => r.GetProperty("board").GetString() == "Esrb");

        rating.GetProperty("board").ValueKind.ShouldBe(JsonValueKind.String);
        rating.GetProperty("code").GetString().ShouldBe("E10+");
        rating.GetProperty("designation").GetString().ShouldBe("Rated");
        rating.GetProperty("minimumAge").GetInt32().ShouldBe(10);
        rating.GetProperty("sourceId").GetString().ShouldBe("user");
    }

    [Fact]
    public async Task ClearContentRating_WithTheChainedETag_RemovesTheUserOverride()
    {
        string titleId = await SeedTitleAsync();
        using var set = await _client.SendAsync(
            Rating(HttpMethod.Put, titleId, "Esrb", await ETagAsync(titleId), new { code = "M" }));
        set.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var cleared = await _client.SendAsync(Rating(HttpMethod.Delete, titleId, "Esrb", set.Headers.ETag!.Tag));

        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("contentRatings").EnumerateArray()
            .ShouldNotContain(r => r.GetProperty("sourceId").GetString() == "user");
    }

    [Theory]
    [InlineData("NotABoard")]
    [InlineData("esrb")]
    [InlineData("0")]
    public async Task SetContentRating_UnknownBoardKey_Returns404Envelope(string board)
    {
        string titleId = await SeedTitleAsync();

        using var response = await _client.SendAsync(
            Rating(HttpMethod.Put, titleId, board, await ETagAsync(titleId), new { code = "E" }));

        using (await ProblemEnvelope.ReadAsync(response, 404, "ReferenceResource.NotFound"))
        { }
    }

    [Fact]
    public async Task SetContentRating_BlankCode_Returns400Envelope()
    {
        string titleId = await SeedTitleAsync();

        using var response = await _client.SendAsync(
            Rating(HttpMethod.Put, titleId, "Esrb", await ETagAsync(titleId), new { code = " " }));

        using (await ProblemEnvelope.ReadAsync(response, 400, "Catalog.InvalidContentRating"))
        { }
    }

    private async Task<string> ETagAsync(string titleId)
    {
        using var response = await _client.GetAsync($"/api/titles/{titleId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private static HttpRequestMessage Rating(HttpMethod method, string titleId, string board, string ifMatch,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, $"/api/titles/{titleId}/content-ratings/{board}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    /// <summary>Seeds a bare title directly and returns its public Sqid.</summary>
    private async Task<string> SeedTitleAsync()
    {
        string systemKey = await TestHelpers.GetFirstSystemKeyAsync(_client);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var platformId = await db.Platforms.Where(x => x.CanonicalKey == systemKey).Select(x => x.Id).SingleAsync();

        string name = $"Content Rating Test Title {Guid.NewGuid():N}";
        var title = new TitleEntity
        {
            PlatformId = platformId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();

        return IdCoder.Encode(title.Id);
    }
}
