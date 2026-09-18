using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for the named-string rating enums
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity"): the rating-board
///     catalog and title content ratings serialize boards/designations as named strings, requests
///     accept named strings only, and invalid values fail with the shared 400 envelope.
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
    public async Task GetRatingBoards_SerializesBoardsAndDesignationsAsNamedStrings()
    {
        var response = await _client.GetAsync("/api/catalog/rating-boards");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var boards = doc.RootElement.GetProperty("boards");

        var boardNames = new List<string>();
        foreach (var board in boards.EnumerateArray())
        {
            var boardValue = board.GetProperty("board");
            boardValue.ValueKind.ShouldBe(JsonValueKind.String,
                "boards must serialize as named strings, never integers");
            boardNames.Add(boardValue.GetString()!);

            foreach (var category in board.GetProperty("categories").EnumerateArray())
            {
                var designation = category.GetProperty("designation");
                designation.ValueKind.ShouldBe(JsonValueKind.String,
                    "designations must serialize as named strings, never integers");
                ValidDesignationNames.ShouldContain(designation.GetString());
            }
        }

        boardNames.ShouldBe(ExpectedBoardNames, ignoreOrder: true);

        // MinimumAge stays a plain int (domain value, not identity — arbiter-ruled).
        var esrb = boards.EnumerateArray().Single(b => b.GetProperty("board").GetString() == "Esrb");
        var e10Plus = esrb.GetProperty("categories").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "E10+");
        e10Plus.GetProperty("designation").GetString().ShouldBe("Rated");
        e10Plus.GetProperty("minimumAge").ValueKind.ShouldBe(JsonValueKind.Number);
        e10Plus.GetProperty("minimumAge").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task SetContentRating_NamedBoardString_PersistsAndSerializesNamedStrings()
    {
        string titleId = await SeedTitleAsync();

        var response = await _client.PatchAsJsonAsync(
            $"/api/titles/{titleId}/content-rating",
            new { board = "Esrb", code = "E10+" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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
    public async Task SetContentRating_UnknownBoardName_Returns400Envelope()
    {
        string titleId = await SeedTitleAsync();

        var response = await _client.PatchAsJsonAsync(
            $"/api/titles/{titleId}/content-rating",
            new { board = "NotABoard", code = "E" });

        await ShouldBeInvalidBodyEnvelopeAsync(response);
    }

    [Fact]
    public async Task SetContentRating_IntegerBoardValue_Returns400Envelope()
    {
        string titleId = await SeedTitleAsync();

        var response = await _client.PatchAsJsonAsync(
            $"/api/titles/{titleId}/content-rating",
            new { board = 0, code = "E" });

        await ShouldBeInvalidBodyEnvelopeAsync(response);
    }

    private static async Task ShouldBeInvalidBodyEnvelopeAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidBody");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
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
