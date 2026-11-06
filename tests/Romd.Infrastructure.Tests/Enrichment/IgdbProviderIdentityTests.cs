using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class IgdbProviderIdentityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task SearchAsync_ObservedExhaustion_ReturnsCompleteWithoutOldTwentyGameCap(int count)
    {
        var calls = 0;
        using var rig = new Rig(_ => Json(++calls == 1 ? Games(1, count) : []));

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.IsError.ShouldBeFalse();
        result.Value.Items.Count.ShouldBe(count);
        result.Value.Completeness.ShouldBe(ProviderGameSearchCompleteness.Complete);
        result.Value.Limit.ShouldBe(500);
        rig.Queries.Count.ShouldBe(count == 100 ? 2 : 1);
        rig.Queries[0].ShouldContain("limit 100;");
        rig.Queries[0].ShouldContain("offset 0;");
        if (count == 100)
            rig.Queries[1].ShouldContain("offset 100;");
    }

    [Fact]
    public async Task SearchAsync_FiveFullResponses_ReturnsBudgetLimitedWithoutInventingExhaustion()
    {
        var calls = 0;
        using var rig = new Rig(_ => Json(Games(calls++ * 100 + 1, 100)));

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.IsError.ShouldBeFalse();
        result.Value.Items.Count.ShouldBe(500);
        result.Value.Completeness.ShouldBe(ProviderGameSearchCompleteness.BudgetLimited);
        rig.Queries.Count.ShouldBe(5);
        rig.Queries.Select((query, page) => query.Contains($"offset {page * 100};", StringComparison.Ordinal)).ShouldAllBe(x => x);
    }

    [Fact]
    public async Task SearchAsync_LiveOrderingRepeatsAnIdentity_ReturnsLimitedDeduplicatedSuggestions()
    {
        var calls = 0;
        using var rig = new Rig(_ => Json(++calls == 1 ? Games(1, 100) : Games(1, 1)));

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.IsError.ShouldBeFalse();
        result.Value.Completeness.ShouldBe(ProviderGameSearchCompleteness.ProviderLimited);
        result.Value.Items.Count.ShouldBe(100);
        rig.Queries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SearchAsync_RawResponseExceedsPageBudget_RejectsRatherThanTruncating()
    {
        using var rig = new Rig(_ => Json(Games(1, 101)));

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.FirstError.Code.ShouldBe("ProviderMatch.InvalidResponse");
        rig.Queries.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("\"platforms\":{}")]
    [InlineData("\"platforms\":[{}]")]
    [InlineData("\"platforms\":[{\"id\":0,\"name\":\"Genesis\"}]")]
    [InlineData("\"platforms\":[{\"id\":\"29\",\"name\":\"Genesis\"}]")]
    [InlineData("\"platforms\":[{\"name\":4}]")]
    [InlineData("\"alternative_names\":\"invalid\"")]
    [InlineData("\"alternative_names\":[{\"name\":\"\"}]")]
    [InlineData("\"release_dates\":{}")]
    [InlineData("\"release_dates\":[null]")]
    [InlineData("\"release_dates\":[{\"y\":\"1994\"}]")]
    [InlineData("\"release_dates\":[{\"y\":0}]")]
    [InlineData("\"release_dates\":[{\"y\":2147483648}]")]
    [InlineData("\"release_dates\":[{\"platform\":{}}]")]
    [InlineData("\"first_release_date\":\"unknown\"")]
    [InlineData("\"first_release_date\":9223372036854775807")]
    public async Task SearchAsync_MalformedPresentEvidence_FailsRatherThanErasingContradictions(string evidence)
    {
        using var rig = new Rig(_ => Raw("[{\"id\":1,\"name\":\"Game\",\"slug\":\"game\"," + evidence + "}]"));

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.FirstError.Code.ShouldBe("ProviderMatch.InvalidResponse");
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("[{}]", true)]
    [InlineData("[{\"id\":1,\"name\":\"Game\"}]", true)]
    [InlineData("[{\"id\":1,\"name\":\"Game\",\"slug\":\"game\"},{\"id\":1,\"name\":\"Game\",\"slug\":\"game\"}]", true)]
    public async Task SearchAsync_InvalidRowsAreNotFilteredIntoACompleteAnswer(string body, bool invalid)
    {
        using var rig = new Rig(_ => Raw(body));

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.IsError.ShouldBe(invalid);
        if (invalid)
            result.FirstError.Code.ShouldBe("ProviderMatch.InvalidResponse");
        else
            result.Value.Completeness.ShouldBe(ProviderGameSearchCompleteness.Complete);
    }

    [Theory]
    [InlineData(29, "AutoAccepted")]
    [InlineData(35, "NeedsReview")]
    public async Task SearchAsync_ProviderPlatformId_CorroboratesOnlyItsMappedLocalSystem(int platformId, string state)
    {
        using var rig = new Rig(_ => Raw($$$"""
            [{"id":1,"name":"Gunstar Heroes","slug":"gunstar-heroes",
              "platforms":[{"id":{{{platformId}}},"name":"Sega Mega Drive/Genesis"}],
              "release_dates":[{"y":1993,"platform":{"id":{{{platformId}}},"name":"Sega Mega Drive/Genesis"}}]}]
            """));
        rig.Mappings.GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["genesis"] = "29" });

        var result = await rig.Adapter.SearchAsync("Gunstar Heroes", default);

        result.IsError.ShouldBeFalse();
        var game = result.Value.Items.ShouldHaveSingleItem();
        var platforms = game.Platforms.ShouldNotBeNull();
        platforms.ShouldContain("Sega Mega Drive/Genesis");
        var facts = new IdentityFacts("Gunstar Heroes", [], [], ["Sega Genesis", "genesis"], "Local system");
        var decision = ProviderIdentityPolicy.Decide(facts,
            new IdentitySearchEvidence([new IdentityCandidate(game.Id, game.Name, [], [], platforms)], true));
        decision.State.ShouldBe(state);
        if (state == "AutoAccepted")
        {
            platforms.ShouldContain("genesis");
            game.Releases.ShouldNotBeNull().ShouldContain(new ProviderReleaseDto(1993, "genesis"));
        }
        else
            platforms.ShouldNotContain("genesis");
    }

    [Fact]
    public async Task SearchAsync_OptionalUnknownDates_RetainsReleaseWithoutInventingYear()
    {
        using var rig = new Rig(_ => Raw("""
            [{"id":1,"name":"Game","slug":"game","first_release_date":null,
              "release_dates":[{"platform":{"name":"SNES"}},{"y":null,"platform":null}]}]
            """));

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.IsError.ShouldBeFalse();
        var game = result.Value.Items.ShouldHaveSingleItem();
        game.Year.ShouldBeNull();
        var releases = game.Releases.ShouldNotBeNull();
        releases.Count.ShouldBe(2);
        releases.ShouldAllBe(release => release.Year == null);
        releases[0].Platform.ShouldBe("SNES");
    }

    [Theory]
    [InlineData("", "query")]
    [InlineData("   ", "query")]
    [InlineData("Game\nwhere id=1;", "query")]
    [InlineData("https://attacker.invalid/games/game", "input")]
    public async Task Identity_InvalidInput_FailsBeforeProviderTransport(string input, string mode)
    {
        using var rig = new Rig(_ => throw new InvalidOperationException("No provider transport expected."));

        var invalid = mode == "query" ? (await rig.Adapter.SearchAsync(input, default)).IsError :
            (await rig.Adapter.ResolveAsync(input, default)).IsError;

        invalid.ShouldBeTrue();
        rig.Requests.ShouldBe(0);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("https://www.igdb.com/games/game-42")]
    public async Task ResolveAsync_ExplicitIdentity_DoesNotDependOnSuggestions(string input)
    {
        using var rig = new Rig(query =>
        {
            query.ShouldNotContain("search ");
            query.ShouldContain("limit 2;");
            return Json(Games(42, 1));
        });

        var result = await rig.Adapter.ResolveAsync(input, default);

        result.IsError.ShouldBeFalse();
        result.Value.Id.ShouldBe("42");
        rig.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_WrongIdentity_FailsInsteadOfReturningAnotherGame()
    {
        using var rig = new Rig(_ => Json(Games(43, 1)));

        var result = await rig.Adapter.ResolveAsync("42", default);

        result.FirstError.Code.ShouldBe("ProviderMatch.InvalidResponse");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAsync_OverByteBudget_RejectsDeclaredAndStreamingBodies(bool declared)
    {
        using var rig = new Rig(_ =>
        {
            HttpContent content = declared ? new ByteArrayContent([]) :
                new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string(' ', 2 * 1024 * 1024 + 1))));
            if (declared)
                content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
            else
                content.Headers.ContentLength.ShouldBeNull();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.FirstError.Code.ShouldBe("ProviderMatch.InvalidResponse");
    }

    [Fact]
    public async Task SearchAsync_ProviderThrottled_RetainsRetryHintWithoutPartialSuccess()
    {
        var calls = 0;
        using var rig = new Rig(_ =>
        {
            if (++calls == 1)
                return Json(Games(1, 100));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var result = await rig.Adapter.SearchAsync("Game", default);

        result.FirstError.Code.ShouldBe("Artwork.ProviderRateLimited");
        ArtworkProviderErrors.GetRetryAfter(result.FirstError).ShouldNotBeNull();
        rig.Queries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SearchAsync_CallerCancelsWhileWaiting_PropagatesCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        });
        using var cancellation = new CancellationTokenSource();
        var pending = rig.Adapter.SearchAsync("Game", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task SearchAsync_RateWaitExceedsWholeSearchDeadline_ReturnsRetryableFailureBeforeIo()
    {
        var limiter = Substitute.For<IProviderRateLimiter>();
        limiter.AcquireAsync("igdb", Arg.Any<CancellationToken>()).Returns(call =>
            Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1)));
        using var rig = new Rig(_ => throw new InvalidOperationException("No provider transport expected."), limiter);

        var result = await rig.Adapter.SearchAsync("Game", default).WaitAsync(TimeSpan.FromSeconds(15));

        result.FirstError.Code.ShouldBe("ProviderMatch.Unavailable");
        rig.Requests.ShouldBe(0);
    }

    private static object[] Games(int start, int count) => Enumerable.Range(start, count)
        .Select(id => (object)new { id, name = $"Game {id}", slug = $"game-{id}" }).ToArray();
    private static HttpResponseMessage Json(object value) => Raw(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Raw(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Rig : IDisposable
    {
        private readonly HttpClient _http;
        public List<string> Queries { get; } = [];
        public int Requests { get; private set; }
        public IProviderIdentityAdapter Adapter { get; }
        public IPlatformAliasRepository Mappings { get; } = Substitute.For<IPlatformAliasRepository>();

        public Rig(Func<string, HttpResponseMessage> games, IProviderRateLimiter? limiter = null)
            : this((query, _) => Task.FromResult(games(query)), limiter) { }

        public Rig(Func<string, CancellationToken, Task<HttpResponseMessage>> games, IProviderRateLimiter? limiter = null)
        {
            _http = new HttpClient(new Handler(async (request, ct) =>
            {
                Requests++;
                if (request.RequestUri!.Host == "id.twitch.tv")
                    return Json(new { access_token = "test-token", expires_in = 3600 });
                var query = await request.Content!.ReadAsStringAsync(ct);
                Queries.Add(query);
                return await games(query, ct);
            }));
            Adapter = new IgdbMetadataProvider(_http,
                Options.Create(new IgdbProviderOptions { ClientId = "test-client", ClientSecret = "test-secret" }),
                limiter ?? Substitute.For<IProviderRateLimiter>(), new MatchConfidenceCalculator(),
                Mappings, TimeProvider.System, NullLogger<IgdbMetadataProvider>.Instance,
                new IgdbAccessTokenCache(TimeProvider.System));
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
