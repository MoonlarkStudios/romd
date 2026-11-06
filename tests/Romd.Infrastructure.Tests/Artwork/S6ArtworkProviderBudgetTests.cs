using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ErrorOr;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Artwork.SteamGridDb;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class S6ArtworkProviderBudgetTests
{
    [Theory]
    [InlineData("{\"success\":true,\"total\":-1,\"data\":[]}")]
    [InlineData("{\"success\":true,\"total\":\"100\",\"data\":[]}")]
    [InlineData("{\"success\":true,\"total\":0.5,\"data\":[]}")]
    [InlineData("{\"success\":true,\"data\":{}}")]
    [InlineData("{\"success\":true}")]
    public async Task SteamGridCandidates_MalformedResponse_ReturnsProviderProblem(string body)
    {
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), new Handler(_ => Response(body)));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster));
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        result.FirstError.NumericType.ShouldBe(502);
    }

    [Fact]
    public async Task SteamGridCandidates_MoreThanFiftyRawRows_RejectsBeforeFiltering()
    {
        var body = JsonSerializer.Serialize(new { success = true, data = Enumerable.Range(0, 51).Select(_ => new { invalid = true }) });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), new Handler(_ => Response(body)));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster));
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Theory]
    [InlineData(5001)]
    [InlineData(10000)]
    public async Task SteamGridCandidates_ExplorationBudgetHasMoreResults_ReturnsErrorRatherThanExhaustion(int total)
    {
        using var provider = new SteamGridDbArtworkProvider(new Credentials(),
            new Handler(_ => Response(JsonSerializer.Serialize(new { success = true, total, data = Enumerable.Range(1, 50).Select(SteamGridImage) }))));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster), page: 99);
        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        result.FirstError.Code.ShouldBe("Artwork.ProviderBrowseLimitExceeded");
    }

    [Theory]
    [InlineData(4951, 1)]
    [InlineData(5000, 50)]
    public async Task SteamGridCandidates_CoherentLastPermittedPage_ReturnsFinalPage(int total, int rawCount)
    {
        using var provider = new SteamGridDbArtworkProvider(new Credentials(),
            new Handler(_ => Response(JsonSerializer.Serialize(new
            {
                success = true,
                total,
                page = 99,
                limit = 50,
                data = Enumerable.Range(1, rawCount).Select(SteamGridImage)
            }))));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster), page: 99);
        result.IsError.ShouldBeFalse();
        result.Value.Items.Count.ShouldBe(rawCount);
        result.Value.Items[0].ProviderAssetId.ShouldBe("1");
        result.Value.NextPage.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 49, 50)]
    [InlineData(1, 50, 1)]
    [InlineData(0, 100, 1)]
    [InlineData(1, 101, 49)]
    [InlineData(99, 5000, 1)]
    [InlineData(0, 1, 0)]
    public async Task SteamGridCandidates_ContradictoryTotal_ReturnsInvalidResponseBeforeCap(int page, int total, int rawCount)
    {
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), new Handler(_ => Response(JsonSerializer.Serialize(
            new { success = true, page, limit = 50, total, data = Enumerable.Range(1, rawCount).Select(SteamGridImage) }))));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster), page);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        result.FirstError.NumericType.ShouldBe(502);
    }

    [Theory]
    [InlineData("\"page\":1,\"limit\":50")]
    [InlineData("\"page\":-1,\"limit\":50")]
    [InlineData("\"page\":0.5,\"limit\":50")]
    [InlineData("\"page\":\"0\",\"limit\":50")]
    [InlineData("\"page\":null,\"limit\":50")]
    [InlineData("\"page\":0,\"limit\":49")]
    [InlineData("\"page\":0,\"limit\":50.5")]
    [InlineData("\"page\":0,\"limit\":\"50\"")]
    [InlineData("\"page\":0,\"limit\":null")]
    public async Task SteamGridCandidates_InvalidPageOrLimitEcho_ReturnsInvalidResponse(string echoed)
    {
        var body = "{\"success\":true,\"total\":0,\"data\":[]," + echoed + "}";
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), new Handler(_ => Response(body)));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster));
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        result.FirstError.NumericType.ShouldBe(502);
    }

    [Fact]
    public async Task SteamGridCandidates_FullLastPageWithoutTotal_ReturnsBrowseLimitExceeded()
    {
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), new Handler(_ => Response(JsonSerializer.Serialize(
            new { success = true, data = Enumerable.Range(1, 50).Select(SteamGridImage) }))));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster), page: 99);
        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        result.FirstError.Code.ShouldBe("Artwork.ProviderBrowseLimitExceeded");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteamGridCandidates_AllRawCandidatesFilteredOut_PreservesContinuation(bool includeTotal)
    {
        var response = new Dictionary<string, object>
        {
            ["success"] = true,
            ["data"] = Enumerable.Range(0, 50).Select(_ => new { invalid = true })
        };
        if (includeTotal)
            response["total"] = 100;
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), new Handler(_ => Response(JsonSerializer.Serialize(response))));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster));
        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldBeEmpty();
        result.Value.NextPage.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    public async Task SteamGridCandidates_EmptyEndPageAfterLiveChange_ReturnsFinalPage(int total)
    {
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), new Handler(_ => Response(JsonSerializer.Serialize(
            new { success = true, page = 1, limit = 50, total, data = Array.Empty<object>() }))));
        var result = await provider.GetCandidatesAsync("42", new(ArtworkRole.Poster), page: 1);
        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldBeEmpty();
        result.Value.NextPage.ShouldBeNull();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"slug\":\"test-game\",\"artworks\":{}}]")]
    [InlineData("[{\"slug\":\"test-game\",\"artworks\":[{\"image_id\":1,\"width\":10,\"height\":10}]}]")]
    public async Task IgdbCandidates_MalformedProviderShape_ReturnsInvalidResponse(string body)
    {
        using var transport = new HttpClient(new Handler(request => request.RequestUri!.Host == "id.twitch.tv"
            ? TokenResponse() : Response(body)));
        var provider = Igdb(transport);
        var result = await provider.BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task IgdbCandidates_FiveHundredRawEntries_ReturnsWholeFiniteCollection()
    {
        var body = JsonSerializer.Serialize(new[] { new { slug = "test-game", artworks = Enumerable.Range(1, 500).Select(IgdbImage) } });
        using var transport = new HttpClient(new Handler(request => request.RequestUri!.Host == "id.twitch.tv"
            ? TokenResponse() : Response(body)));
        var result = await Igdb(transport).BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Value.Items.Count.ShouldBe(500);
        result.Value.Items.Select(item => item.ProviderAssetId).Distinct().Count().ShouldBe(500);
        result.Value.NextPage.ShouldBeNull();
    }

    [Fact]
    public async Task IgdbCandidates_OverRawEntryBudget_RejectsBeforeInvalidCandidateFiltering()
    {
        var body = JsonSerializer.Serialize(new[] { new
        {
            slug = "test-game", artworks = Enumerable.Range(1, 501).Select(_ => new { image_id = "../unsafe", width = 10, height = 10 })
        } });
        using var transport = new HttpClient(new Handler(request => request.RequestUri!.Host == "id.twitch.tv"
            ? TokenResponse() : Response(body)));
        var result = await Igdb(transport).BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task IgdbCandidates_RawBudgetIncludesUnrequestedImageKinds()
    {
        var body = JsonSerializer.Serialize(new[] { new
        {
            slug = "test-game", artworks = Enumerable.Range(1, 250).Select(IgdbImage),
            screenshots = Enumerable.Range(251, 251).Select(IgdbImage)
        } });
        using var transport = new HttpClient(new Handler(request => request.RequestUri!.Host == "id.twitch.tv"
            ? TokenResponse() : Response(body)));
        var result = await Igdb(transport).BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task IgdbCandidates_ResponseExceedsTwoMiB_ReturnsInvalidResponse()
    {
        var body = JsonSerializer.Serialize(new[] { new
        {
            slug = "test-game", artworks = new[] { IgdbImage(1) }, padding = new string('x', 2 * 1024 * 1024)
        } });
        using var transport = new HttpClient(new Handler(request => request.RequestUri!.Host == "id.twitch.tv"
            ? TokenResponse() : Response(body)));
        var result = await Igdb(transport).BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task IgdbCandidates_UnadvertisedOversizedStream_StopsReadingAtBudget()
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { new
        {
            slug = "test-game", artworks = new[] { IgdbImage(1) }, padding = new string('x', 3 * 1024 * 1024)
        } }));
        using var stream = new UnseekableStream(bytes);
        using var transport = new HttpClient(new Handler(request => request.RequestUri!.Host == "id.twitch.tv"
            ? TokenResponse() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var result = await Igdb(transport).BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        stream.BytesRead.ShouldBeGreaterThan(2 * 1024 * 1024);
        stream.BytesRead.ShouldBeLessThanOrEqualTo(2 * 1024 * 1024 + 16384);
    }

    private static IgdbMetadataProvider Igdb(HttpClient transport) => new(transport,
        Options.Create(new IgdbProviderOptions { ClientId = "s6-test-client", ClientSecret = "s6-test-secret" }),
        Substitute.For<IProviderRateLimiter>(), new MatchConfidenceCalculator(), Substitute.For<IPlatformAliasRepository>(),
            TimeProvider.System, NullLogger<IgdbMetadataProvider>.Instance, new IgdbAccessTokenCache(TimeProvider.System));

    private static object IgdbImage(int id) => new { image_id = $"s6_{id}", width = 1920, height = 1080 };

    private static object SteamGridImage(int id) => new
    {
        id,
        width = 600,
        height = 900,
        style = "alternate",
        url = $"https://cdn2.steamgriddb.com/grid/s6_{id}.png",
        thumb = $"https://cdn2.steamgriddb.com/thumb/s6_{id}.jpg"
    };

    private static HttpResponseMessage TokenResponse() => Response("{\"access_token\":\"s6-token\",\"expires_in\":3600,\"token_type\":\"bearer\"}");

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Credentials : ISteamGridDbCredentials
    {
        public Task<string?> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult<string?>("s6-controlled-key");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
}
