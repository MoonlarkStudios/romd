using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class IgdbScopedSetupTests
{
    [Fact]
    public async Task ProductionScopes_StreamSetupIsOnceAndArtworkSetupFollowsActualUse()
    {
        var protection = new EphemeralDataProtectionProvider();
        var stored = Settings(protection, "client", "secret");
        var store = Substitute.For<IIgdbProviderSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(stored);
        var aliases = Substitute.For<IPlatformAliasRepository>();
        aliases.GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["snes"] = "19" });
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Token("token");
            if (request.RequestUri.AbsolutePath.EndsWith("/multiquery"))
            {
                int count = Regex.Matches(await request.Content!.ReadAsStringAsync(ct), "query games").Count;
                return Json(Enumerable.Range(0, count).Select(index => new
                {
                    name = $"q{index}",
                    result = new[] { new { id = 42, name = "Game 42", slug = "game-42" } }
                }));
            }
            return Games();
        });
        await using var services = BuildServices(handler, protection, store, aliases);
        await using var streamingScope = services.CreateAsyncScope();
        var metadata = streamingScope.ServiceProvider.GetRequiredService<IgdbMetadataProvider>();
        await metadata.InitializeAsync();
        await metadata.InitializeAsync();
        int returned = 0;

        await foreach (var (_, result) in metadata.EnrichAsync(Titles()))
        {
            result.Outcome.ShouldBe(EnrichmentOutcome.Found);
            returned++;
        }

        returned.ShouldBe(25);
        await store.Received(1).LoadAsync(Arg.Any<CancellationToken>());
        await aliases.Received(1).GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>());
        for (int title = 0; title < 2; title++)
        {
            await using var scope = services.CreateAsyncScope();
            var artwork = scope.ServiceProvider.GetRequiredService<IgdbArtworkProvider>();
            var identity = (IProviderIdentityAdapter)scope.ServiceProvider.GetRequiredService<IgdbMetadataProvider>();
            foreach (var role in new[] { ArtworkRole.Poster, ArtworkRole.Hero, ArtworkRole.Backdrop })
                (await artwork.GetCandidatesAsync("42", new ProviderArtworkQuery(role))).IsError.ShouldBeFalse();

            // Plain artwork browsing needs one runtime settings snapshot and no platform map.
            await store.Received(2 + title * 3).LoadAsync(Arg.Any<CancellationToken>());
            await aliases.Received(1 + title).GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>());
            (await identity.ResolveAsync("42", CancellationToken.None)).IsError.ShouldBeFalse();
            await aliases.Received(2 + title).GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>());
            // Availability intentionally observes current configuration on every call.
            (await identity.GetAvailabilityAsync(CancellationToken.None)).Configured.ShouldBeTrue();
            (await identity.GetAvailabilityAsync(CancellationToken.None)).Configured.ShouldBeTrue();
        }
        await store.Received(7).LoadAsync(Arg.Any<CancellationToken>());
        await aliases.Received(3).GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>());

        static async IAsyncEnumerable<EnrichmentContext> Titles()
        {
            for (int title = 0; title < 25; title++)
                yield return new EnrichmentContext { TitleName = "Game 42", PlatformShortName = "snes", ExistingExternalId = "42" };
            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionScope_CredentialsRotateAfterUnauthorized_ReplaysWithMatchingNewClientAndToken(bool changeClientId)
    {
        var protection = new EphemeralDataProtectionProvider();
        var stored = Settings(protection, "old-client", "old-secret");
        var store = Substitute.For<IIgdbProviderSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => stored);
        int tokenRequests = 0;
        int gameRequests = 0;
        var bodies = new List<string>();
        var messages = new List<HttpRequestMessage>();
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                tokenRequests++;
                var form = await request.Content!.ReadAsStringAsync(ct);
                form.ShouldContain(tokenRequests == 1 ? "client_secret=old-secret" : "client_secret=new-secret");
                return Token(tokenRequests == 1 ? "old-token" : "new-token");
            }
            messages.Add(request);
            bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            gameRequests++;
            if (gameRequests == 1)
            {
                stored = Settings(protection, changeClientId ? "new-client" : "old-client", "new-secret");
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            request.Headers.GetValues("Client-ID").Single().ShouldBe(changeClientId ? "new-client" : "old-client");
            request.Headers.Authorization!.Parameter.ShouldBe("new-token");
            return Games();
        });
        await using var services = BuildServices(handler, protection, store, Substitute.For<IPlatformAliasRepository>());
        await using var scope = services.CreateAsyncScope();
        var artwork = scope.ServiceProvider.GetRequiredService<IgdbArtworkProvider>();

        (await artwork.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster))).IsError.ShouldBeFalse();

        tokenRequests.ShouldBe(2);
        gameRequests.ShouldBe(2);
        messages[0].ShouldNotBeSameAs(messages[1]);
        bodies[0].ShouldBe(bodies[1]);
        await store.Received(2).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionScope_BatchAuthorization_UsesAtMostOneReplayForAllMembers(bool permanentFailure)
    {
        var protection = new EphemeralDataProtectionProvider();
        var store = Substitute.For<IIgdbProviderSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Settings(protection, "client", "secret"));
        var aliases = Substitute.For<IPlatformAliasRepository>();
        aliases.GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["snes"] = "19" });
        int tokenRequests = 0;
        var queries = new List<string>();
        var requests = new List<HttpRequestMessage>();
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Token($"token-{++tokenRequests}");
            request.RequestUri.AbsolutePath.ShouldBe("/v4/multiquery", "a terminal401 must never fan out to individual members");
            requests.Add(request);
            queries.Add(await request.Content!.ReadAsStringAsync(ct));
            if (permanentFailure || queries.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return Json(Enumerable.Range(0, 10).Select(index => new
            {
                name = $"q{index}",
                result = new[] { new { id = 42 + index, name = $"Game {index}", slug = $"game-{index}" } }
            }));
        });
        await using var services = BuildServices(handler, protection, store, aliases);
        await using var scope = services.CreateAsyncScope();
        var metadata = scope.ServiceProvider.GetRequiredService<IgdbMetadataProvider>();
        var results = new List<(EnrichmentContext Context, EnrichmentResult Result)>();

        await foreach (var pair in metadata.EnrichAsync(Batch()))
            results.Add(pair);

        queries.Count.ShouldBe(2);
        tokenRequests.ShouldBe(2);
        queries[1].ShouldBe(queries[0]);
        requests[1].ShouldNotBeSameAs(requests[0]);
        results.Select(pair => pair.Context.ExistingExternalId).ShouldBe(Enumerable.Range(42, 10).Select(id => id.ToString()));
        results.ShouldAllBe(pair => pair.Result.Outcome == (permanentFailure ? EnrichmentOutcome.Error : EnrichmentOutcome.Found));

        static async IAsyncEnumerable<EnrichmentContext> Batch()
        {
            for (int index = 0; index < 10; index++)
                yield return new EnrichmentContext { TitleName = $"Game {index}", PlatformShortName = "snes", ExistingExternalId = (42 + index).ToString() };
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProductionScope_DisabledAfterUnauthorized_DoesNotRefreshOrReplay()
    {
        var protection = new EphemeralDataProtectionProvider();
        var stored = Settings(protection, "client", "secret");
        var store = Substitute.For<IIgdbProviderSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => stored);
        int tokenRequests = 0;
        int gameRequests = 0;
        using var handler = new Handler((request, ct) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            { tokenRequests++; return Task.FromResult(Token("token")); }
            gameRequests++;
            stored = stored with { Enabled = false, Revision = Guid.NewGuid() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        await using var services = BuildServices(handler, protection, store, Substitute.For<IPlatformAliasRepository>());
        await using var scope = services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IgdbArtworkProvider>()
            .GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster));

        result.IsError.ShouldBeTrue();
        tokenRequests.ShouldBe(1);
        gameRequests.ShouldBe(1);
        await store.Received(2).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductionScope_CancelledDuringUnauthorizedRefresh_PropagatesWithoutAnotherGameRequest()
    {
        var protection = new EphemeralDataProtectionProvider();
        var store = Substitute.For<IIgdbProviderSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Settings(protection, "client", "secret"));
        using var cancellation = new CancellationTokenSource();
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int tokenRequests = 0;
        int gameRequests = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                if (++tokenRequests == 2)
                {
                    refreshing.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                return Token("token");
            }
            gameRequests++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        await using var services = BuildServices(handler, protection, store, Substitute.For<IPlatformAliasRepository>());
        await using var scope = services.CreateAsyncScope();
        var pending = scope.ServiceProvider.GetRequiredService<IgdbArtworkProvider>()
            .GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster), ct: cancellation.Token);
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        tokenRequests.ShouldBe(2);
        gameRequests.ShouldBe(1);
    }

    private static ServiceProvider BuildServices(HttpMessageHandler handler, IDataProtectionProvider protection,
        IIgdbProviderSettingsStore store, IPlatformAliasRepository aliases)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddRomdAdminCurationServices();
        services.AddSingleton(protection);
        services.AddSingleton(store);
        services.AddScoped(_ => aliases);
        services.AddSingleton(Substitute.For<IProviderRateLimiter>());
        services.AddHttpClient(nameof(IgdbMetadataProvider)).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IgdbProviderStoredSettings Settings(IDataProtectionProvider protection, string client, string secret) =>
        new(true, client, protection.CreateProtector("romd.metadata-providers.igdb.credentials.v1").Protect(secret),
            Guid.NewGuid(), null, null, null, null);
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Token(string value) => Json(new { access_token = value, expires_in = 3600 });
    private static HttpResponseMessage Games() => Json(new[] { new { id = 42, name = "Game 42", slug = "game-42" } });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
