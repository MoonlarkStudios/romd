using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class IgdbAccessTokenCacheTests
{
    [Fact]
    public async Task BrowseAsync_SeparateTitleClients_ReuseOneTokenWithoutRetainingTheFirstHttpClient()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var requests = 0;
        var authorizations = new List<string?>();
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
            {
                requests++;
                return Task.FromResult(Token("shared-token"));
            }
            authorizations.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(Games());
        }

        for (int title = 1; title <= 20; title++)
        {
            // This is the same scoped artwork -> scoped metadata -> singleton token-cache chain
            // used by bulk title publication. Each HTTP client is disposed before the next title.
            using var scope = new TitleClient(Send, clock, cache);
            (await scope.Artwork.GetCandidatesAsync(title.ToString(), new(ArtworkRole.Poster))).IsError.ShouldBeFalse();
        }

        requests.ShouldBe(1);
        authorizations.Count.ShouldBe(20);
        authorizations.ShouldAllBe(token => token == "shared-token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowseAsync_CredentialsRotate_ReplacesTheTokenAndEvictsThePriorCredentialSet(bool rotateClientId)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var requests = 0;
        var authorizations = new List<string?>();
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
                return Task.FromResult(Token($"token-{++requests}"));
            authorizations.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(Games());
        }
        using var original = new TitleClient(Send, clock, cache);
        using var replacement = new TitleClient(Send, clock, cache,
            rotateClientId ? "replacement-client" : "test-client", rotateClientId ? "test-secret" : "replacement-secret");

        (await original.BrowseAsync()).IsError.ShouldBeFalse();
        (await replacement.BrowseAsync()).IsError.ShouldBeFalse();
        (await original.BrowseAsync()).IsError.ShouldBeFalse();

        requests.ShouldBe(3, "rotating back must not revive an evicted credential set's cached token");
        authorizations.ShouldBe(["token-1", "token-2", "token-3"]);
    }

    [Fact]
    public async Task BrowseAsync_ConcurrentExpiredClients_RefreshOnceAndUseTheNewToken()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var authorizations = new ConcurrentBag<string?>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
            {
                int number = Interlocked.Increment(ref requests);
                if (number == 2)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                return Token($"token-{number}");
            }
            authorizations.Add(request.Headers.Authorization?.Parameter);
            return Games();
        }
        using (var warmup = new TitleClient(Send, clock, cache))
            (await warmup.BrowseAsync()).IsError.ShouldBeFalse();
        clock.Advance(TimeSpan.FromHours(1));
        var clients = Enumerable.Range(0, 20).Select(_ => new TitleClient(Send, clock, cache)).ToArray();
        try
        {
            var pending = clients.Select(client => client.BrowseAsync()).ToArray();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            requests.ShouldBe(2);
            release.TrySetResult();

            (await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10))).ShouldAllBe(result => !result.IsError);

            requests.ShouldBe(2);
            authorizations.Count(token => token == "token-1").ShouldBe(1);
            authorizations.Count(token => token == "token-2").ShouldBe(20);
        }
        finally
        {
            release.TrySetResult();
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task BrowseAsync_ShortLivedToken_RemainsReusableBeforeItsRefreshMargin(int lifetimeSeconds)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var requests = 0;
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(IsAuthentication(request) ? Token($"token-{++requests}", lifetimeSeconds) : Games());
        using var first = new TitleClient(Send, clock, cache);
        using var second = new TitleClient(Send, clock, cache);

        (await first.BrowseAsync()).IsError.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(lifetimeSeconds * 0.5));
        (await second.BrowseAsync()).IsError.ShouldBeFalse();
        requests.ShouldBe(1);
        clock.Advance(TimeSpan.FromSeconds(lifetimeSeconds));
        (await second.BrowseAsync()).IsError.ShouldBeFalse();
        requests.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowseAsync_RefreshFails_NeverUsesExpiredOrRotatedTokenAndCanRetry(bool rotateCredentials)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var requests = 0;
        var gameRequests = 0;
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
            {
                int number = ++requests;
                return Task.FromResult(number == 2 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Token($"token-{number}"));
            }
            gameRequests++;
            return Task.FromResult(Games());
        }
        using (var first = new TitleClient(Send, clock, cache))
            (await first.BrowseAsync()).IsError.ShouldBeFalse();
        if (!rotateCredentials)
            clock.Advance(TimeSpan.FromHours(1));
        using var next = new TitleClient(Send, clock, cache, secret: rotateCredentials ? "rotated-secret" : "test-secret");

        (await next.BrowseAsync()).IsError.ShouldBeTrue();
        gameRequests.ShouldBe(1);
        (await next.BrowseAsync()).IsError.ShouldBeFalse();
        requests.ShouldBe(3);
        gameRequests.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowseAsync_CancelledRefreshOrWaiter_DoesNotPoisonAnotherClient(bool cancelRefresh)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (!IsAuthentication(request))
                return Games();
            int number = Interlocked.Increment(ref requests);
            if (number == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return Token($"token-{number}");
        }
        using var first = new TitleClient(Send, clock, cache);
        using var second = new TitleClient(Send, clock, cache);
        var owner = first.BrowseAsync(cancelRefresh ? cancellation.Token : CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiter = second.BrowseAsync(cancelRefresh ? CancellationToken.None : cancellation.Token);

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelRefresh ? owner : waiter);
        release.TrySetResult();
        (await (cancelRefresh ? waiter : owner).WaitAsync(TimeSpan.FromSeconds(10))).IsError.ShouldBeFalse();
        (await second.BrowseAsync()).IsError.ShouldBeFalse();
        requests.ShouldBe(cancelRefresh ? 2 : 1);
    }

    [Fact]
    public async Task BrowseAsync_RefreshCompletes_DisposesAuthenticationResponseAndCredentialForm()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var body = new ObservedContent("""{"access_token":"test-token","expires_in":3600}""");
        HttpContent? form = null;
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (!IsAuthentication(request))
                return Task.FromResult(Games());
            form = request.Content;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
        using var client = new TitleClient(Send, clock, cache);

        (await client.BrowseAsync()).IsError.ShouldBeFalse();

        body.Disposed.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => form!.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("artwork")]
    [InlineData("metadata")]
    [InlineData("identity")]
    [InlineData("steam")]
    public async Task RequestAsync_Unauthorized_RefreshesAndReplaysOnceForCurrentClient(string path)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var tokenRequests = 0;
        var gameRequests = 0;
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
                return Task.FromResult(Token($"token-{++tokenRequests}"));
            return Task.FromResult(++gameRequests == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Games());
        }
        using var first = new TitleClient(Send, clock, cache);
        using var next = new TitleClient(Send, clock, cache);

        (await first.RequestSucceedsAsync(path)).ShouldBeTrue();
        gameRequests.ShouldBe(2);
        (await next.RequestSucceedsAsync(path)).ShouldBeTrue();
        tokenRequests.ShouldBe(2);
        gameRequests.ShouldBe(3);
    }

    [Fact]
    public async Task EnrichAsync_PermanentUnauthorizedBatch_StopsAfterOneRefreshWithoutIndividualFallback()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var tokenRequests = 0;
        var gameRequests = 0;
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
                return Task.FromResult(Token($"token-{++tokenRequests}"));
            gameRequests++;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/multiquery")
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Games());
        }
        using var client = new TitleClient(Send, clock, cache);
        var results = new List<EnrichmentResult>();

        await foreach (var (_, result) in client.Metadata.EnrichAsync(OneTitle()))
            results.Add(result);

        results.ShouldHaveSingleItem().Outcome.ShouldBe(EnrichmentOutcome.Error);
        tokenRequests.ShouldBe(2);
        gameRequests.ShouldBe(2);

        static async IAsyncEnumerable<EnrichmentContext> OneTitle()
        {
            yield return new EnrichmentContext { TitleName = "Game 42", PlatformShortName = "snes", ExistingExternalId = "42" };
            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowseAsync_LateUnauthorizedFromOldRequest_CannotEvictTheRefreshedEntry(bool sameTokenValue)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenRequests = 0;
        var gameRequests = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
            {
                int number = Interlocked.Increment(ref tokenRequests);
                return Token(sameTokenValue ? "same-token" : $"token-{number}");
            }
            if (Interlocked.Increment(ref gameRequests) == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return Games();
        }
        using var original = new TitleClient(Send, clock, cache);
        using var refreshed = new TitleClient(Send, clock, cache);
        (await original.BrowseAsync()).IsError.ShouldBeFalse();
        var olderRequest = original.BrowseAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromHours(1));
        try
        {
            (await refreshed.BrowseAsync()).IsError.ShouldBeFalse();
            release.TrySetResult();
            (await olderRequest.WaitAsync(TimeSpan.FromSeconds(10))).IsError.ShouldBeFalse();

            (await refreshed.BrowseAsync()).IsError.ShouldBeFalse();

            tokenRequests.ShouldBe(2);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("artwork")]
    [InlineData("metadata")]
    [InlineData("identity")]
    [InlineData("steam")]
    public async Task RequestAsync_SecondUnauthorized_StopsAndDisposesBothResponses(string path)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        var tokenRequests = 0;
        var bodies = new List<ObservedContent>();
        var queries = new List<string>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
                return Token($"token-{++tokenRequests}");
            queries.Add(await request.Content!.ReadAsStringAsync(ct));
            var body = new ObservedContent("unauthorized");
            bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = body };
        }
        using var client = new TitleClient(Send, clock, cache);

        (await client.RequestSucceedsAsync(path)).ShouldBeFalse();

        tokenRequests.ShouldBe(2);
        bodies.Count.ShouldBe(2);
        bodies.ShouldAllBe(body => body.Disposed);
        queries[1].ShouldBe(queries[0], "the replay must rebuild the same request body");
    }

    [Fact]
    public async Task BrowseAsync_CancelledAfterUnauthorized_DoesNotRefreshOrReplay()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new IgdbAccessTokenCache(clock);
        using var cancellation = new CancellationTokenSource();
        var tokenRequests = 0;
        var gameRequests = 0;
        var body = new ObservedContent("unauthorized");
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsAuthentication(request))
                return Task.FromResult(Token($"token-{++tokenRequests}"));
            gameRequests++;
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = body });
        }
        using var client = new TitleClient(Send, clock, cache);

        await Should.ThrowAsync<OperationCanceledException>(() => client.BrowseAsync(cancellation.Token));

        body.Disposed.ShouldBeTrue();
        tokenRequests.ShouldBe(1);
        gameRequests.ShouldBe(1);
    }

    private static bool IsAuthentication(HttpRequestMessage request) => request.RequestUri!.Host == "id.twitch.tv";
    private static HttpResponseMessage Token(string token, int seconds = 3600) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":{{seconds}}}""", Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Games() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""[{"id":42,"name":"Game 42","slug":"game-42"}]""", Encoding.UTF8, "application/json")
    };

    private sealed class TitleClient : IDisposable
    {
        private readonly HttpClient _client;
        public IgdbArtworkProvider Artwork { get; }
        public IgdbMetadataProvider Metadata { get; }

        public TitleClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
            TimeProvider clock, IgdbAccessTokenCache tokens, string clientId = "test-client", string secret = "test-secret")
        {
            _client = new HttpClient(new Handler(send));
            var aliases = Substitute.For<IPlatformAliasRepository>();
            aliases.GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, string> { ["snes"] = "19" });
            Metadata = new IgdbMetadataProvider(_client,
                Options.Create(new IgdbProviderOptions { ClientId = clientId, ClientSecret = secret }),
                Substitute.For<IProviderRateLimiter>(), new MatchConfidenceCalculator(),
                aliases, clock, NullLogger<IgdbMetadataProvider>.Instance, tokens);
            Artwork = new IgdbArtworkProvider(Metadata, Substitute.For<IHttpClientFactory>());
        }

        public Task<ErrorOr.ErrorOr<ProviderArtworkPage>> BrowseAsync(CancellationToken ct = default) =>
            Artwork.GetCandidatesAsync("42", new(ArtworkRole.Poster), ct: ct);
        public async Task<bool> RequestSucceedsAsync(string path) => path switch
        {
            "artwork" => !(await BrowseAsync()).IsError,
            "metadata" => (await Metadata.EnrichSingleAsync(new EnrichmentContext
            { TitleName = "Game 42", PlatformShortName = "snes" })).Outcome == EnrichmentOutcome.Found,
            "identity" => !(await ((IProviderIdentityAdapter)Metadata).ResolveAsync("42", CancellationToken.None)).IsError,
            "steam" => !(await Metadata.GetSteamIdentityAsync("42", CancellationToken.None)).IsError,
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };
        public void Dispose() => _client.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class ObservedContent(string body) : StringContent(body, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
