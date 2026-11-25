using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Providers.Igdb;

namespace Romd.Infrastructure.Tests.Enrichment;

public class IgdbMetadataProviderTests
{
    [Theory]
    [InlineData("https://images.igdb.com.evil.example/igdb/image/upload/t_original/hero1.jpg")]
    [InlineData("https://images.igdb.com/igdb/image/upload/t_original/hero1.jpg?redirect=1")]
    [InlineData("https://images.igdb.com/igdb/image/upload/t_original/other.jpg")]
    [InlineData("http://127.0.0.1/hero1.jpg")]
    public async Task DownloadArtworkAsync_RejectsUntrustedLocationBeforeTransport(string url)
    {
        var metadata = CreateProvider(_ => throw new InvalidOperationException("No transport expected"));
        var provider = new IgdbArtworkProvider(metadata, Substitute.For<IHttpClientFactory>());
        (await provider.DownloadAsync("igdb", "42", "hero1", ArtworkRole.Hero, url)).FirstError.Code
            .ShouldBe("Artwork.ProviderInvalidRequest");
    }

    [Fact]
    public async Task BrowseArtworkAsync_UsesLinkedGameAndReturnsOnlyRequestedRole()
    {
        var provider = CreateProvider(request => request.RequestUri!.Host == "id.twitch.tv"
            ? CreateTokenResponse() : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [{"id":42,"slug":"test-game","cover":{"image_id":"cover1","width":600,"height":900},
                    "artworks":[{"image_id":"hero1","width":1920,"height":1080},
                    {"image_id":"../bad","width":1920,"height":1080}],
                    "screenshots":[{"image_id":"screen1","width":1280,"height":720},
                    {"image_id":"screen2","width":640,"height":480}]}]
                    """, Encoding.UTF8, "application/json")
            });
        var posters = await provider.BrowseArtworkAsync("42", new(ArtworkRole.Poster), 0, CancellationToken.None);
        posters.Value.Items.Single().ProviderAssetId.ShouldBe("cover1");
        var heroes = await provider.BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        var hero = heroes.Value.Items.Single();
        hero.ProviderGameId.ShouldBe("42");
        hero.AssetUrl.AbsoluteUri.ShouldBe("https://images.igdb.com/igdb/image/upload/t_original/hero1.jpg");
        hero.SourcePageUrl.ShouldBe("https://www.igdb.com/games/test-game");
        var screenshots = await provider.BrowseArtworkAsync("42", new(ArtworkRole.Hero, MediaType: MediaType.Screenshot), 0, CancellationToken.None);
        screenshots.Value.Items.Select(item => item.ProviderAssetId).ShouldBe(["screen1", "screen2"]);
        screenshots.Value.Items.ShouldAllBe(item => item.MediaType == MediaType.Screenshot);
        (await provider.BrowseArtworkAsync("42", new(ArtworkRole.Hero, MediaType: MediaType.Logo), 0, CancellationToken.None)).IsError.ShouldBeTrue();
        (await provider.BrowseArtworkAsync("42", new(ArtworkRole.Hero, Style: "alternate"), 0, CancellationToken.None)).IsError.ShouldBeTrue();
        (await provider.BrowseArtworkAsync("42;", new(ArtworkRole.Hero), 0, CancellationToken.None)).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task BrowseArtworkAsync_Poster_AddsRegionalCoversAfterTheMainCoverWithTheirRegion()
    {
        string? query = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return CreateTokenResponse();
            query = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [{"id":42,"slug":"test-game","cover":{"image_id":"cover1","width":600,"height":900},
                    "game_localizations":[
                    {"cover":{"image_id":"cover_jp","width":600,"height":850},"region":{"name":"Japan"}},
                    {"cover":{"image_id":"cover1","width":600,"height":900},"region":{"name":"North America"}},
                    {"cover":{"image_id":"cover_eu","width":600,"height":860},"region":{"name":"bad\nregion"}},
                    {"region":{"name":"Korea"}}]}]
                    """, Encoding.UTF8, "application/json")
            };
        });

        var posters = await provider.BrowseArtworkAsync("42", new(ArtworkRole.Poster), 0, CancellationToken.None);

        query.ShouldNotBeNull().ShouldContain("game_localizations.region.name");
        posters.Value.Items.Select(item => (item.ProviderAssetId, item.Region))
            .ShouldBe([("cover1", (string?)null), ("cover_jp", "Japan"), ("cover_eu", null)]);
        posters.Value.Items.ShouldAllBe(item => item.MediaType == MediaType.Cover);
        var heroes = await provider.BrowseArtworkAsync("42", new(ArtworkRole.Hero), 0, CancellationToken.None);
        heroes.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ArtworkCapabilities_ExcludeGameLogosAndLogoRequestsAreRejectedBeforeTransport()
    {
        var metadata = CreateProvider(_ => throw new InvalidOperationException("No transport expected"));
        var provider = new IgdbArtworkProvider(metadata,
            Substitute.For<IHttpClientFactory>());

        provider.Capabilities.Roles.ShouldNotContain(role => role.Role == ArtworkRole.Logo);
        provider.Capabilities.MediaTypes!.ShouldNotContain(MediaType.Logo);
        (await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Logo)))
            .FirstError.Code.ShouldBe("Artwork.ProviderInvalidRequest");
        (await provider.DownloadAsync("igdb", "42", "logo1", ArtworkRole.Logo,
            "https://images.igdb.com/igdb/image/upload/t_original/logo1.jpg"))
            .FirstError.Code.ShouldBe("Artwork.ProviderInvalidRequest");
    }

    [Fact]
    public async Task FetchSingleAsync_Artwork_KeepsMeasuredRoleCandidatesSeparateFromScreenshots()
    {
        var provider = CreateProvider(request => request.RequestUri!.Host == "id.twitch.tv"
            ? CreateTokenResponse() : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [{"name":"q0","result":[{"id":12345,"name":"Super Mario World","summary":"A platform game",
                      "cover":{"url":"//images.igdb.com/igdb/image/upload/t_thumb/cover1.jpg","width":900,"height":600},
                      "artworks":[{"url":"//images.igdb.com/igdb/image/upload/t_thumb/hero1.jpg","width":1920,"height":620},
                                  {"url":"//images.igdb.com/igdb/image/upload/t_thumb/hero2.jpg","width":1600,"height":900}],
                      "screenshots":[{"url":"//images.igdb.com/igdb/image/upload/t_thumb/screenshot1.jpg"}]}]}]
                    """, Encoding.UTF8, "application/json")
            });
        var result = await provider.FetchSingleAsync(new("12345", new()
        { TitleName = "Super Mario World", PlatformShortName = "snes" }));
        result.Outcome.ShouldBe(EnrichmentOutcome.Found);
        result.Artwork.Count.ShouldBe(5);
        result.Artwork.Count(x => x.Role == ArtworkRole.Backdrop).ShouldBe(2);
        result.Artwork.Single(x => x.Role == ArtworkRole.Poster).Width.ShouldBe(900);
        result.Artwork.ShouldNotContain(x => x.AssetId == "screenshot1");
        result.Artwork.ShouldAllBe(x => x.Url.StartsWith("https://images.igdb.com/igdb/image/upload/t_original/"));
    }

    private static readonly IReadOnlyDictionary<string, string> DefaultPlatformMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["snes"] = "19",
            ["genesis"] = "29",
            ["nes"] = "18"
        };

    private static IgdbMetadataProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        TimeProvider? timeProvider = null,
        IReadOnlyDictionary<string, string>? platformMappings = null)
    {
        var messageHandler = new FakeHttpMessageHandler(handler);
        var httpClient = new HttpClient(messageHandler);

        var options = Options.Create(new IgdbProviderOptions
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret"
        });

        var rateLimiter = Substitute.For<IProviderRateLimiter>();

        var aliasRepository = Substitute.For<IPlatformAliasRepository>();
        aliasRepository
            .GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>())
            .Returns(platformMappings ?? DefaultPlatformMappings);

        return new IgdbMetadataProvider(
            httpClient,
            options,
            rateLimiter,
            new MatchConfidenceCalculator(),
            aliasRepository,
            timeProvider ?? TimeProvider.System,
            NullLogger<IgdbMetadataProvider>.Instance,
            new IgdbAccessTokenCache(timeProvider ?? TimeProvider.System));
    }

    private static HttpResponseMessage CreateTokenResponse()
    {
        string tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "test-token",
            expires_in = 3600,
            token_type = "bearer"
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(tokenJson, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateGameResponse(int id = 12345, string name = "Super Mario World")
    {
        var games = new[]
        {
            new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["summary"] = "A platform game" }
        };
        string json = JsonSerializer.Serialize(games);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateGameResponseWithAgeRatings(params object[] ageRatings)
    {
        var games = new[]
        {
            new
            {
                id = 12345,
                name = "Super Mario World",
                summary = "A platform game",
                age_ratings = ageRatings
            }
        };
        string json = JsonSerializer.Serialize(new[] { new { name = "q0", result = games } });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateMultiQueryResponse(params (string name, int id, string gameName)[] entries)
    {
        var responses = entries.Select(e => new Dictionary<string, object>
        {
            ["name"] = e.name,
            ["result"] = new[]
            {
                new Dictionary<string, object> { ["id"] = e.id, ["name"] = e.gameName, ["summary"] = "A game" }
            }
        }).ToList();
        string json = JsonSerializer.Serialize(responses);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task FetchAsync_GameId_FetchesById()
    {
        string? capturedQuery = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            capturedQuery = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateMultiQueryResponse(("q0", 99999, "Fetched By Id"));
        });

        var request = new MetadataFetchRequest("99999", new()
        {
            TitleName = "Super Mario World",
            PlatformShortName = "snes"
        });

        var results = new List<(MetadataFetchRequest, EnrichmentResult)>();
        await foreach (var pair in provider.FetchAsync(ToAsyncEnumerable(request)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(1);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        results[0].Item2.ExternalId.ShouldBe("99999");

        capturedQuery.ShouldNotBeNull();
        capturedQuery.ShouldContain("where id = 99999");
        capturedQuery.ShouldNotContain("search");
    }

    [Fact]
    public async Task FetchAsync_UnmappedLocalPlatform_FetchesById()
    {
        string? capturedQuery = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            capturedQuery = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateMultiQueryResponse(("q0", 55555, "Fetched By Id"));
        });

        var request = new MetadataFetchRequest("55555", new()
        {
            TitleName = "Some Game",
            PlatformShortName = "unsupported_platform_xyz"
        });

        var results = new List<(MetadataFetchRequest, EnrichmentResult)>();
        await foreach (var pair in provider.FetchAsync(ToAsyncEnumerable(request)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(1);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        results[0].Item2.ExternalId.ShouldBe("55555");

        capturedQuery.ShouldNotBeNull();
        capturedQuery.ShouldContain("where id = 55555");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("42; limit 500")]
    public async Task FetchSingleAsync_InvalidId_RejectsBeforeTransport(string id)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No HTTP request should be sent."));

        var result = await provider.FetchSingleAsync(new(id, new()
        {
            TitleName = "Super Mario World",
            PlatformShortName = "snes"
        }));

        result.Outcome.ShouldBe(EnrichmentOutcome.Error);
    }

    [Fact]
    public async Task FetchAsync_BatchWithUnexpectedGameId_DoesNotApplyItOrLoseOtherResults()
    {
        var provider = CreateProvider(request => request.RequestUri!.Host == "id.twitch.tv" ? CreateTokenResponse() :
            CreateMultiQueryResponse(("q1", 44, "Wrong Game"), ("q0", 42, "First Game"), ("q2", 43, "Last Game")));
        var first = new MetadataFetchRequest("42", new() { TitleName = "First Game", PlatformShortName = "snes" });
        var wrong = new MetadataFetchRequest("43", new() { TitleName = "Second Game", PlatformShortName = "snes" });
        var last = new MetadataFetchRequest("43", new() { TitleName = "Last Game", PlatformShortName = "snes" });
        var results = new List<(MetadataFetchRequest Request, EnrichmentResult Result)>();

        await foreach (var pair in provider.FetchAsync(ToAsyncEnumerable(first, wrong, last)))
            results.Add(pair);

        results[0].Request.ShouldBeSameAs(first);
        results[0].Result.ExternalId.ShouldBe("42");
        results[1].Request.ShouldBeSameAs(wrong);
        results[1].Result.Outcome.ShouldBe(EnrichmentOutcome.Error);
        results[1].Result.ExternalId.ShouldBeNull();
        results[2].Request.ShouldBeSameAs(last);
        results[2].Result.ExternalId.ShouldBe("43");
    }

    [Fact]
    public async Task FetchAsync_MultiQueryFailsForFetchById_FallsBackToIndividual()
    {
        int requestCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            requestCount++;
            // Multi-query fails
            if (request.RequestUri?.AbsolutePath == "/v4/multiquery")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            // Both individual lookups must request the same IDs as the failed batch.
            var query = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            query.ShouldNotContain("search");
            return requestCount switch
            {
                2 when query.Contains("where id = 111;", StringComparison.Ordinal) => CreateGameResponse(111),
                3 when query.Contains("where id = 222;", StringComparison.Ordinal) => CreateGameResponse(222),
                _ => throw new InvalidOperationException("Unexpected individual lookup.")
            };
        });

        var contexts = new[]
        {
            new MetadataFetchRequest("111", new() { TitleName = "Game 1", PlatformShortName = "snes" }),
            new MetadataFetchRequest("222", new() { TitleName = "Game 2", PlatformShortName = "genesis" })
        };

        var results = new List<(MetadataFetchRequest, EnrichmentResult)>();
        await foreach (var pair in provider.FetchAsync(ToAsyncEnumerable(contexts)))
        {
            results.Add(pair);
        }

        // Both should succeed via individual fallback
        results.Count.ShouldBe(2);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        results[1].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);

        // Multi-query (1) + individual fallbacks (2) = 3 IGDB requests
        requestCount.ShouldBe(3);
    }

    [Fact]
    public async Task FetchAsync_QueryUsesExpandedAgeRatingFields()
    {
        string? capturedQuery = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            capturedQuery = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateMultiQueryResponse(("q0", 12345, "Super Mario World"));
        });

        var request = new MetadataFetchRequest("12345", new() { TitleName = "Super Mario World", PlatformShortName = "snes" });

        await DrainAsync(provider.FetchAsync(ToAsyncEnumerable(request)));

        capturedQuery.ShouldNotBeNull();
        capturedQuery.ShouldContain("age_ratings.id");
        capturedQuery.ShouldContain("age_ratings.organization.name");
        capturedQuery.ShouldContain("age_ratings.rating_category.rating");
        capturedQuery.ShouldContain("age_ratings.rating_content_descriptions.description");
        capturedQuery.ShouldContain("age_ratings.synopsis");
        capturedQuery.ShouldNotContain("age_ratings.category");
        capturedQuery.ShouldNotContain("age_ratings.rating,");
    }

    [Fact]
    public async Task FetchAsync_MultiBoardAgeRatings_MapsClaimsWithDescriptorsSynopsisAndLegacyScalar()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            return CreateGameResponseWithAgeRatings(
                new
                {
                    id = 1001,
                    organization = new { name = "ESRB" },
                    rating_category = new { rating = "T" },
                    rating_content_descriptions = new[]
                    {
                        new { description = "Fantasy Violence" },
                        new { description = "Mild Language" }
                    },
                    synopsis = "ESRB synopsis"
                },
                new
                {
                    id = 1002,
                    organization = new { name = "PEGI" },
                    rating_category = new { rating = "Twelve" },
                    rating_content_descriptions = new[] { new { description = "Violence" } },
                    synopsis = "PEGI synopsis"
                },
                new
                {
                    id = 1003,
                    organization = new { name = "GRAC" },
                    rating_category = new { rating = "TESTING" },
                    rating_content_descriptions = Array.Empty<object>(),
                    synopsis = (string?)null
                },
                new
                {
                    id = 1004,
                    organization = new { name = "ACB" },
                    rating_category = new { rating = "RC" },
                    rating_content_descriptions = Array.Empty<object>(),
                    synopsis = "Refused by board"
                });
        });

        var request = new MetadataFetchRequest("12345", new() { TitleName = "Super Mario World", PlatformShortName = "snes" });

        var results = new List<(MetadataFetchRequest, EnrichmentResult)>();
        await foreach (var pair in provider.FetchAsync(ToAsyncEnumerable(request)))
        {
            results.Add(pair);
        }

        var data = results.Single().Item2.Data;
        data.ShouldNotBeNull();
        data.ContentRatings.ShouldNotBeNull();
        data.ContentRatings.Count.ShouldBe(4);

        var esrb = data.ContentRatings.Single(r => r.Board == RatingBoard.Esrb);
        esrb.RawCode.ShouldBe("T");
        esrb.ExternalRatingId.ShouldBe("1001");
        esrb.Descriptors.ShouldBe(["Fantasy Violence", "Mild Language"]);
        esrb.Synopsis.ShouldBe("ESRB synopsis");

        var pegi = data.ContentRatings.Single(r => r.Board == RatingBoard.Pegi);
        pegi.RawCode.ShouldBe("Twelve");
        pegi.Descriptors.ShouldBe(["Violence"]);
        pegi.Synopsis.ShouldBe("PEGI synopsis");

        var grac = data.ContentRatings.Single(r => r.Board == RatingBoard.Grac);
        RatingBoardCatalog.TryResolve(grac.Board, grac.RawCode)!.Designation.ShouldBe(RatingDesignation.RatingPending);

        var acb = data.ContentRatings.Single(r => r.Board == RatingBoard.Acb);
        acb.Synopsis.ShouldBe("Refused by board");
        RatingBoardCatalog.TryResolve(acb.Board, acb.RawCode)!.Designation.ShouldBe(
            RatingDesignation.RefusedClassification);
    }

    [Fact]
    public async Task FetchAsync_UnknownOrganizationOrCategory_DropsClaimWithoutGuessing()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            return CreateGameResponseWithAgeRatings(
                new
                {
                    id = 2001,
                    organization = new { name = "NOT_A_BOARD" },
                    rating_category = new { rating = "E" },
                    rating_content_descriptions = Array.Empty<object>()
                },
                new
                {
                    id = 2002,
                    organization = new { name = "PEGI" },
                    rating_category = new { rating = "NOT A RATING" },
                    rating_content_descriptions = Array.Empty<object>()
                },
                new
                {
                    id = 2003,
                    organization = new { name = "ESRB" },
                    rating_content_descriptions = Array.Empty<object>()
                },
                new
                {
                    id = 2004,
                    organization = new { name = "CLASS_IND" },
                    rating_category = new { rating = "14" },
                    rating_content_descriptions = Array.Empty<object>()
                });
        });

        var request = new MetadataFetchRequest("12345", new() { TitleName = "Super Mario World", PlatformShortName = "snes" });

        var results = new List<(MetadataFetchRequest, EnrichmentResult)>();
        await foreach (var pair in provider.FetchAsync(ToAsyncEnumerable(request)))
        {
            results.Add(pair);
        }

        var claims = results.Single().Item2.Data!.ContentRatings;
        claims.ShouldNotBeNull();
        claims.Count.ShouldBe(1);
        claims[0].Board.ShouldBe(RatingBoard.ClassInd);
        claims[0].RawCode.ShouldBe("14");
        claims[0].ExternalRatingId.ShouldBe("2004");
    }

    [Fact]
    public async Task FetchAsync_TokenStillValid_ReusesAccessToken()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var tokenRequestCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                tokenRequestCount++;
                return CreateTokenResponse();
            }

            return CreateMultiQueryResponse(("q0", 12345, "Super Mario World"));
        }, timeProvider);

        var request = new MetadataFetchRequest("12345", new() { TitleName = "Super Mario World", PlatformShortName = "snes" });

        await DrainAsync(provider.FetchAsync(ToAsyncEnumerable(request)));

        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await DrainAsync(provider.FetchAsync(ToAsyncEnumerable(request)));

        tokenRequestCount.ShouldBe(1);

        timeProvider.Advance(TimeSpan.FromMinutes(31));
        await DrainAsync(provider.FetchAsync(ToAsyncEnumerable(request)));

        tokenRequestCount.ShouldBe(2);
    }

    private static async IAsyncEnumerable<MetadataFetchRequest> ToAsyncEnumerable(
        params MetadataFetchRequest[] requests)
    {
        foreach (var request in requests)
        {
            yield return request;
        }

        await Task.CompletedTask;
    }

    private static async Task DrainAsync(IAsyncEnumerable<(MetadataFetchRequest, EnrichmentResult)> results)
    {
        await foreach (var _ in results)
        {
        }
    }

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
