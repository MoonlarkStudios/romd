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
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

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
        var provider = new Romd.Infrastructure.Artwork.IgdbArtworkProvider(metadata, Substitute.For<IHttpClientFactory>());
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
    public async Task ArtworkCapabilities_ExcludeGameLogosAndLogoRequestsAreRejectedBeforeTransport()
    {
        var metadata = CreateProvider(_ => throw new InvalidOperationException("No transport expected"));
        var provider = new Romd.Infrastructure.Artwork.IgdbArtworkProvider(metadata,
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
    public async Task EnrichSingleAsync_Artwork_KeepsMeasuredRoleCandidatesSeparateFromScreenshots()
    {
        var provider = CreateProvider(request => request.RequestUri!.Host == "id.twitch.tv"
            ? CreateTokenResponse() : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [{"id":12345,"name":"Super Mario World","summary":"A platform game",
                      "cover":{"url":"//images.igdb.com/igdb/image/upload/t_thumb/cover1.jpg","width":900,"height":600},
                      "artworks":[{"url":"//images.igdb.com/igdb/image/upload/t_thumb/hero1.jpg","width":1920,"height":620},
                                  {"url":"//images.igdb.com/igdb/image/upload/t_thumb/hero2.jpg","width":1600,"height":900}],
                      "screenshots":[{"url":"//images.igdb.com/igdb/image/upload/t_thumb/screenshot1.jpg"}]}]
                    """, Encoding.UTF8, "application/json")
            });
        var result = await provider.EnrichSingleAsync(new EnrichmentContext
            { TitleName = "Super Mario World", PlatformShortName = "snes" });
        result.Outcome.ShouldBe(EnrichmentOutcome.Found);
        result.Artwork.Count.ShouldBe(5);
        result.Artwork.Count(x => x.Role == ArtworkRole.Backdrop).ShouldBe(2);
        result.Artwork.Single(x => x.Role == ArtworkRole.Poster).Width.ShouldBe(900);
        result.Artwork.ShouldNotContain(x => x.AssetId == "screenshot1");
        result.Artwork.ShouldAllBe(x => x.Url.StartsWith("https://images.igdb.com/igdb/image/upload/t_original/"));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

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
            NullLogger<IgdbMetadataProvider>.Instance);
    }

    private static HttpResponseMessage CreateTokenResponse()
    {
        string tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "test-token", expires_in = 3600, token_type = "bearer"
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
        string json = JsonSerializer.Serialize(games);
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
    public async Task EnrichAsync_UnsupportedPlatform_YieldsImmediately()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Should not be called"));

        var context = new EnrichmentContext { TitleName = "Test Game", PlatformShortName = "unsupported_platform_xyz" };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(1);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.PlatformNotSupported);
    }

    [Fact]
    public async Task EnrichAsync_SingleContext_SearchesIndividually()
    {
        var requestPaths = new List<string>();
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            requestPaths.Add(request.RequestUri?.AbsolutePath ?? "");
            return CreateGameResponse();
        });

        var context = new EnrichmentContext { TitleName = "Super Mario World", PlatformShortName = "snes" };

        var results = new List<(EnrichmentContext Context, EnrichmentResult Result)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(1);
        results[0].Context.ShouldBeSameAs(context);
        results[0].Result.Outcome.ShouldBe(EnrichmentOutcome.Found);
        results[0].Result.ExternalId.ShouldBe("12345");

        // Search contexts go directly to /v4/games, not multi-query
        requestPaths.ShouldContain("/v4/games");
        requestPaths.ShouldNotContain("/v4/multiquery");
    }

    [Fact]
    public async Task EnrichAsync_MultipleSearchContexts_AllProcessedIndividually()
    {
        int igdbRequestCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            igdbRequestCount++;
            return CreateGameResponse(igdbRequestCount, $"Game {igdbRequestCount}");
        });

        var contexts = new[]
        {
            new EnrichmentContext { TitleName = "Game 1", PlatformShortName = "snes" },
            new EnrichmentContext { TitleName = "Game 2", PlatformShortName = "genesis" },
            new EnrichmentContext { TitleName = "Game 3", PlatformShortName = "nes" }
        };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(contexts)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(3);
        for (int i = 0; i < 3; i++)
        {
            results[i].Item1.ShouldBeSameAs(contexts[i]);
            results[i].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        }

        // Each search context makes its own request (search + possible wildcard fallback)
        igdbRequestCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task EnrichAsync_ExistingExternalId_FetchesById()
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

        var context = new EnrichmentContext
        {
            TitleName = "Super Mario World", PlatformShortName = "snes", ExistingExternalId = "99999"
        };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
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
    public async Task EnrichAsync_UnmappedPlatformWithExternalId_FetchesById()
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

        var context = new EnrichmentContext
        {
            TitleName = "Some Game", PlatformShortName = "unsupported_platform_xyz", ExistingExternalId = "55555"
        };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(1);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        results[0].Item2.ExternalId.ShouldBe("55555");

        capturedQuery.ShouldNotBeNull();
        capturedQuery.ShouldContain("where id = 55555");
    }

    [Fact]
    public async Task EnrichAsync_TenSearchContexts_IndividualRequests()
    {
        int igdbRequestCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            igdbRequestCount++;
            return CreateGameResponse(100 + igdbRequestCount, $"Game {igdbRequestCount}");
        });

        var contexts = Enumerable.Range(0, 10)
            .Select(i => new EnrichmentContext { TitleName = $"Game {i}", PlatformShortName = "snes" })
            .ToArray();

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(contexts)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(10);
        // Each search context makes its own individual request
        igdbRequestCount.ShouldBeGreaterThanOrEqualTo(10);
        for (int i = 0; i < 10; i++)
        {
            results[i].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        }
    }

    [Fact]
    public async Task EnrichAsync_FifteenSearchContexts_AllProcessedIndividually()
    {
        int igdbRequestCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            igdbRequestCount++;
            return CreateGameResponse(100 + igdbRequestCount, $"Game {igdbRequestCount}");
        });

        var contexts = Enumerable.Range(0, 15)
            .Select(i => new EnrichmentContext { TitleName = $"Game {i}", PlatformShortName = "snes" })
            .ToArray();

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(contexts)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(15);
        // Each search context makes its own request (no multi-query for searches)
        igdbRequestCount.ShouldBeGreaterThanOrEqualTo(15);
    }

    [Fact]
    public async Task EnrichAsync_MixedSearchAndFetchById_SplitsCorrectly()
    {
        var requestPaths = new List<string>();
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            string path = request.RequestUri?.AbsolutePath ?? "";
            requestPaths.Add(path);

            if (path == "/v4/multiquery")
            {
                // Multi-query for fetch-by-ID
                return CreateMultiQueryResponse(("q1", 99999, "Fetched By Id"));
            }

            // Individual search request
            return CreateGameResponse(1, "Search Result");
        });

        var contexts = new[]
        {
            new EnrichmentContext { TitleName = "Game 1", PlatformShortName = "snes" },
            new EnrichmentContext { TitleName = "Game 2", PlatformShortName = "snes", ExistingExternalId = "99999" }
        };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(contexts)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(2);
        // Fetch-by-ID results come first (from multi-query), then search results
        results[0].Item2.ExternalId.ShouldBe("99999");
        results[1].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);

        // Multi-query used for fetch-by-ID, /v4/games used for search
        requestPaths.ShouldContain("/v4/multiquery");
        requestPaths.ShouldContain("/v4/games");
    }

    [Fact]
    public async Task EnrichAsync_MultiQueryFailsForFetchById_FallsBackToIndividual()
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

            // Individual fallback succeeds
            return CreateGameResponse();
        });

        var contexts = new[]
        {
            new EnrichmentContext { TitleName = "Game 1", PlatformShortName = "snes", ExistingExternalId = "111" },
            new EnrichmentContext
            {
                TitleName = "Game 2", PlatformShortName = "genesis", ExistingExternalId = "222"
            }
        };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(contexts)))
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
    public async Task EnrichAsync_UnsupportedPlatformsBypassBuffer()
    {
        int igdbRequestCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            igdbRequestCount++;
            return CreateGameResponse(1, "Game 1");
        });

        var contexts = new[]
        {
            new EnrichmentContext { TitleName = "Unsupported Game", PlatformShortName = "unsupported_xyz" },
            new EnrichmentContext { TitleName = "Game 1", PlatformShortName = "snes" }
        };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(contexts)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(2);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.PlatformNotSupported);
        results[1].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);

        // Only requests for the supported platform
        igdbRequestCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task EnrichAsync_MultipleResults_SelectsBestConfidenceMatch()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            // Return multiple games — the best name match should be selected
            object[] games = new object[]
            {
                new Dictionary<string, object>
                {
                    ["id"] = 111, ["name"] = "Mario Teaches Typing", ["summary"] = "Not a match"
                },
                new Dictionary<string, object>
                {
                    ["id"] = 222, ["name"] = "Mario Paint", ["summary"] = "The actual game"
                },
                new Dictionary<string, object>
                {
                    ["id"] = 333, ["name"] = "Mario Party", ["summary"] = "Also not it"
                }
            };
            string json = JsonSerializer.Serialize(games);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var context = new EnrichmentContext { TitleName = "Mario Paint", PlatformShortName = "snes" };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(1);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        // Should pick "Mario Paint" (id 222) as it has the highest confidence match
        results[0].Item2.ExternalId.ShouldBe("222");
    }

    [Fact]
    public async Task EnrichAsync_SearchContextGoesDirectlyToIndividualSearch()
    {
        var requestPaths = new List<string>();
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            requestPaths.Add(request.RequestUri?.AbsolutePath ?? "");
            return CreateGameResponse(42, "Mario Paint");
        });

        var context = new EnrichmentContext { TitleName = "Mario Paint", PlatformShortName = "snes" };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(1);
        results[0].Item2.Outcome.ShouldBe(EnrichmentOutcome.Found);
        results[0].Item2.ExternalId.ShouldBe("42");

        // Search goes directly to /v4/games, never multi-query
        requestPaths.ShouldContain("/v4/games");
        requestPaths.ShouldNotContain("/v4/multiquery");
    }

    [Fact]
    public async Task EnrichAsync_QueryUsesExpandedAgeRatingFields()
    {
        string? capturedQuery = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.Host == "id.twitch.tv")
            {
                return CreateTokenResponse();
            }

            capturedQuery = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateGameResponse();
        });

        var context = new EnrichmentContext { TitleName = "Super Mario World", PlatformShortName = "snes" };

        await DrainAsync(provider.EnrichAsync(ToAsyncEnumerable(context)));

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
    public async Task EnrichAsync_MultiBoardAgeRatings_MapsClaimsWithDescriptorsSynopsisAndLegacyScalar()
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

        var context = new EnrichmentContext { TitleName = "Super Mario World", PlatformShortName = "snes" };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
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
    public async Task EnrichAsync_UnknownOrganizationOrCategory_DropsClaimWithoutGuessing()
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

        var context = new EnrichmentContext { TitleName = "Super Mario World", PlatformShortName = "snes" };

        var results = new List<(EnrichmentContext, EnrichmentResult)>();
        await foreach (var pair in provider.EnrichAsync(ToAsyncEnumerable(context)))
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
    public async Task EnrichAsync_TokenStillValid_ReusesAccessToken()
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

            return CreateGameResponse();
        }, timeProvider);

        var context = new EnrichmentContext { TitleName = "Super Mario World", PlatformShortName = "snes" };

        await DrainAsync(provider.EnrichAsync(ToAsyncEnumerable(context)));

        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await DrainAsync(provider.EnrichAsync(ToAsyncEnumerable(context)));

        tokenRequestCount.ShouldBe(1);

        timeProvider.Advance(TimeSpan.FromMinutes(31));
        await DrainAsync(provider.EnrichAsync(ToAsyncEnumerable(context)));

        tokenRequestCount.ShouldBe(2);
    }

    private static async IAsyncEnumerable<EnrichmentContext> ToAsyncEnumerable(
        params EnrichmentContext[] contexts)
    {
        foreach (var ctx in contexts)
        {
            yield return ctx;
        }

        await Task.CompletedTask;
    }

    private static async Task DrainAsync(IAsyncEnumerable<(EnrichmentContext, EnrichmentResult)> results)
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
