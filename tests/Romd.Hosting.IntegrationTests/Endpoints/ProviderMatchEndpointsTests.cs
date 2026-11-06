using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     A title's provider matches under <c>/api/titles/{titleId}/provider-matches</c>: each match is its own
///     concurrency-controlled resource (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProviderMatchEndpointsTests(IntegrationTestFixture fixture)
{
    private const string Provider = "fakeprovider";

    [Fact]
    public async Task Anonymous_CannotReadProviderMatches()
    {
        using var client = fixture.CreateClient();
        (await client.GetAsync($"/api/titles/{IdCoder.Encode(1)}/provider-matches")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OrdinaryUser_CannotSearchOrChangeProviderMatches()
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        (await client.GetAsync("/api/metadata-providers/igdb/games?query=Metroid")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/metadata-providers/igdb/game-resolution?input=42")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/titles/{IdCoder.Encode(1)}/provider-matches/igdb")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_CanReadDirectoryWithoutAdminSettingsAccess()
    {
        var titleId = await SeedTitleAsync();
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Manager]);
        var response = await client.GetAsync($"/api/titles/{titleId}/provider-matches");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("clientSecret", Case.Insensitive);
        body.ShouldNotContain("apiKey", Case.Insensitive);
    }

    [Fact]
    public async Task UnknownTitleOrProvider_Is404Envelope()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using (var title = await client.GetAsync($"/api/titles/{IdCoder.Encode(int.MaxValue)}/provider-matches"))
        using (await ProblemEnvelope.ReadAsync(title, 404, "Catalog.TitleNotFound"))
        { }
        var titleId = await SeedTitleAsync();
        using (var provider = await client.GetAsync($"/api/titles/{titleId}/provider-matches/no-such-provider"))
        using (await ProblemEnvelope.ReadAsync(provider, 404, "ProviderMatch.UnknownProvider"))
        { }
    }

    [Fact]
    public async Task Get_ReturnsTheMatchWithItsRevisionAsTheETag_WithoutRevalidating()
    {
        await using var host = WithProvider();
        var client = host.Client;
        var titleId = await SeedTitleAsync();

        using var response = await client.GetAsync($"/api/titles/{titleId}/provider-matches/{Provider}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var match = (await response.Content.ReadFromJsonAsync<TitleProviderMatchDto>())!;
        response.Headers.ETag!.Tag.ShouldBe($"\"{match.Revision}\"");
        response.Headers.ETag.IsWeak.ShouldBeFalse();
        match.PendingJobId.ShouldBeNull();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/titles/{titleId}/provider-matches/{Provider}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag.Tag).ShouldBeTrue();
        (await client.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("PUT", "")]
    [InlineData("POST", "/confirm")]
    [InlineData("DELETE", "")]
    public async Task Write_MissingIfMatchIs428_StaleIs412_AndNeitherWrites(string method, string suffix)
    {
        await using var host = WithProvider();
        var client = host.Client;
        var titleId = await SeedTitleAsync(linkedGameId: "7");
        var etag = await ETagAsync(client, titleId);
        var titleRevision = await StoredTitleRevisionAsync(titleId);
        var path = $"/api/titles/{titleId}/provider-matches/{Provider}{suffix}";
        object? body = method == "PUT" ? new { externalId = "42" } : null;

        using (var missing = await client.SendAsync(Request(new HttpMethod(method), path, null, body)))
        using (await ProblemEnvelope.ReadAsync(missing, 428, "ProviderMatch.PreconditionRequired"))
        { }
        using (var stale = await client.SendAsync(Request(new HttpMethod(method), path, $"\"{Guid.NewGuid()}\"", body)))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "ProviderMatch.PreconditionFailed"))
        { }

        (await ETagAsync(client, titleId)).ShouldBe(etag);
        (await StoredTitleRevisionAsync(titleId)).ShouldBe(titleRevision);
    }

    [Fact]
    public async Task SetConfirmUnlink_EachRequireTheMatchETag_AndChainTheReturnedOne()
    {
        await using var host = WithProvider();
        var client = host.Client;
        var titleId = await SeedTitleAsync();
        var titleRevision = await StoredTitleRevisionAsync(titleId);
        var path = $"/api/titles/{titleId}/provider-matches/{Provider}";

        using var set = await client.SendAsync(Request(HttpMethod.Put, path, await ETagAsync(client, titleId),
            new { externalId = "42" }));
        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var linked = (await set.Content.ReadFromJsonAsync<TitleProviderMatchDto>())!;
        set.Headers.ETag!.Tag.ShouldBe($"\"{linked.Revision}\"");
        (linked.State, linked.ExternalId).ShouldBe(("Confirmed", "42"));
        // The write queued the enrichment that applies the match, and rotated the title's revision.
        linked.PendingJobId.ShouldNotBeNull();
        (await client.GetAsync($"/api/jobs/{linked.PendingJobId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredTitleRevisionAsync(titleId)).ShouldNotBe(titleRevision);
        (await ETagAsync(client, titleId)).ShouldBe(set.Headers.ETag.Tag);

        using var confirm = await client.SendAsync(Request(HttpMethod.Post, $"{path}/confirm", set.Headers.ETag.Tag));
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());
        var confirmed = (await confirm.Content.ReadFromJsonAsync<TitleProviderMatchDto>())!;
        confirm.Headers.ETag!.Tag.ShouldBe($"\"{confirmed.Revision}\"");
        confirmed.Revision.ShouldNotBe(linked.Revision);

        using (var stale = await client.SendAsync(Request(HttpMethod.Delete, path, set.Headers.ETag.Tag)))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "ProviderMatch.PreconditionFailed"))
        { }
        // Unlinking answers the unlinked match with its new validator.
        using var unlink = await client.SendAsync(Request(HttpMethod.Delete, path, confirm.Headers.ETag.Tag));
        unlink.StatusCode.ShouldBe(HttpStatusCode.OK, await unlink.Content.ReadAsStringAsync());
        var unlinked = (await unlink.Content.ReadFromJsonAsync<TitleProviderMatchDto>())!;
        (unlinked.State, unlinked.ExternalId, unlinked.PendingJobId).ShouldBe(("Unlinked", null, null));
        unlink.Headers.ETag!.Tag.ShouldBe($"\"{unlinked.Revision}\"");
        (await ETagAsync(client, titleId)).ShouldBe(unlink.Headers.ETag.Tag);
    }

    [Fact]
    public async Task Confirm_WithoutALink_Is409AndWritesNothing()
    {
        await using var host = WithProvider();
        var client = host.Client;
        var titleId = await SeedTitleAsync();
        var titleRevision = await StoredTitleRevisionAsync(titleId);

        using var confirm = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/titles/{titleId}/provider-matches/{Provider}/confirm", await ETagAsync(client, titleId)));

        using (await ProblemEnvelope.ReadAsync(confirm, 409, "ProviderMatch.NotLinked"))
        { }
        (await StoredTitleRevisionAsync(titleId)).ShouldBe(titleRevision);
    }

    [Fact]
    public async Task Set_AnIdTheProviderCannotResolve_Is400KeyedByExternalId_AndWritesNothing()
    {
        await using var host = WithProvider(resolve: _ => ProviderMatchErrors.NotFound);
        var client = host.Client;
        var titleId = await SeedTitleAsync();
        var etag = await ETagAsync(client, titleId);

        using var set = await client.SendAsync(Request(HttpMethod.Put, $"/api/titles/{titleId}/provider-matches/{Provider}",
            etag, new { externalId = "404" }));

        using var problem = await ProblemEnvelope.ReadAsync(set, 400, "ProviderMatch.Unresolvable");
        problem.RootElement.GetProperty("errors").TryGetProperty("externalId", out _).ShouldBeTrue();
        (await ETagAsync(client, titleId)).ShouldBe(etag);
    }

    [Fact]
    public async Task Set_StaleETag_Is412BeforeTheProviderIsAsked()
    {
        var resolved = 0;
        await using var host = WithProvider(resolve: id =>
        {
            Interlocked.Increment(ref resolved);
            return new ProviderGameDto(id, "Game", "https://example.com/game");
        });
        var titleId = await SeedTitleAsync();

        using var stale = await host.Client.SendAsync(Request(HttpMethod.Put,
            $"/api/titles/{titleId}/provider-matches/{Provider}", $"\"{Guid.NewGuid()}\"", new { externalId = "42" }));

        using (await ProblemEnvelope.ReadAsync(stale, 412, "ProviderMatch.PreconditionFailed"))
        { }
        resolved.ShouldBe(0);
    }

    [Fact]
    public async Task Set_ConcurrentWritersWithOneETag_OneWinsAndTheOtherIs412()
    {
        await using var host = WithProvider();
        var titleId = await SeedTitleAsync();
        var etag = await ETagAsync(host.Client, titleId);
        var path = $"/api/titles/{titleId}/provider-matches/{Provider}";

        var responses = await Task.WhenAll(new[] { "41", "42" }.Select(async externalId =>
        {
            using var response = await host.Client.SendAsync(Request(HttpMethod.Put, path, etag, new { externalId }));
            return (int)response.StatusCode;
        }));

        responses.Order().ShouldBe([200, 412]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrUnconfiguredProvider_Is409OnWritesAndSearch(bool enabled, bool configured)
    {
        await using var host = WithProvider(new ProviderAvailability(enabled, configured));
        var client = host.Client;
        var titleId = await SeedTitleAsync(linkedGameId: "7");
        var path = $"/api/titles/{titleId}/provider-matches/{Provider}";
        var etag = $"\"{Guid.NewGuid()}\"";

        foreach (var request in new[]
                 {
                     Request(HttpMethod.Put, path, etag, new { externalId = "42" }),
                     Request(HttpMethod.Post, $"{path}/confirm", etag),
                     Request(HttpMethod.Delete, path, etag),
                     Request(HttpMethod.Get, $"/api/metadata-providers/{Provider}/games?query=Metroid", null)
                 })
        {
            using (request)
            using (var response = await client.SendAsync(request))
            using (await ProblemEnvelope.ReadAsync(response, 409, "ProviderMatch.ProviderNotConfigured"))
            { }
        }
        // A disabled provider has no match to read; an unconfigured one still shows its local match.
        using var read = await client.GetAsync(path);
        if (enabled)
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        else
            using (await ProblemEnvelope.ReadAsync(read, 409, "ProviderMatch.ProviderNotConfigured"))
            { }
    }

    [Fact]
    public async Task Set_AnInvalidProviderResponse_Is502AndWritesNothing()
    {
        await using var host = WithProvider(resolve: _ => Romd.Admin.Application.Artwork.Providers.ArtworkProviderErrors.InvalidResponse());
        var titleId = await SeedTitleAsync();
        var etag = await ETagAsync(host.Client, titleId);

        using var set = await host.Client.SendAsync(Request(HttpMethod.Put, $"/api/titles/{titleId}/provider-matches/{Provider}",
            etag, new { externalId = "42" }));

        using (await ProblemEnvelope.ReadAsync(set, 502, "Artwork.ProviderInvalidResponse"))
        { }
        (await ETagAsync(host.Client, titleId)).ShouldBe(etag);
    }

    [Fact]
    public async Task UnavailableUpstream_Is503_AndARateLimitSaysWhenToRetry()
    {
        var retryAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await using var host = WithProvider(resolve: id => id == "limited"
            ? Romd.Admin.Application.Artwork.Providers.ArtworkProviderErrors.RateLimited(retryAt)
            : ProviderMatchErrors.Unavailable);
        var client = host.Client;
        var titleId = await SeedTitleAsync();

        using var set = await client.SendAsync(Request(HttpMethod.Put, $"/api/titles/{titleId}/provider-matches/{Provider}",
            await ETagAsync(client, titleId), new { externalId = "42" }));
        using (await ProblemEnvelope.ReadAsync(set, 503, "ProviderMatch.Unavailable"))
        { }
        set.Headers.RetryAfter.ShouldBeNull();

        using var limited = await client.GetAsync($"/api/metadata-providers/{Provider}/game-resolution?input=limited");
        using (await ProblemEnvelope.ReadAsync(limited, 503, "Artwork.ProviderRateLimited"))
        { }
        limited.Headers.RetryAfter!.Date.ShouldBe(retryAt);
    }

    [Fact]
    public async Task Resolve_IsIndependentOfSearch_UnknownProviderIs404_AndUnresolvableInputIs400()
    {
        await using var host = WithProvider(resolve: id => id == "42"
            ? new ProviderGameDto(id, "Game", "https://example.com/game")
            : ProviderMatchErrors.NotFound);
        var client = host.Client;

        using var found = await client.GetAsync($"/api/metadata-providers/{Provider}/game-resolution?input=42");
        found.StatusCode.ShouldBe(HttpStatusCode.OK, await found.Content.ReadAsStringAsync());
        (await found.Content.ReadFromJsonAsync<ProviderGameDto>())!.Id.ShouldBe("42");
        using var unknown = await client.GetAsync("/api/metadata-providers/no-such-provider/games?query=Metroid");
        using (await ProblemEnvelope.ReadAsync(unknown, 404, "ProviderMatch.UnknownProvider"))
        { }
        using var unresolvable = await client.GetAsync($"/api/metadata-providers/{Provider}/game-resolution?input=7");
        using var problem = await ProblemEnvelope.ReadAsync(unresolvable, 400, "ProviderMatch.Unresolvable");
        problem.RootElement.GetProperty("errors").TryGetProperty("input", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.Complete)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited)]
    [InlineData(ProviderGameSearchCompleteness.BudgetLimited)]
    public async Task Search_AnswersExplicitSuggestionCompleteness_WithoutResolvingAnIdentity(ProviderGameSearchCompleteness completeness)
    {
        var searched = 0;
        var resolved = 0;
        await using var host = WithProvider(resolve: _ =>
        {
            resolved++;
            return ProviderMatchErrors.NotFound;
        }, search: query =>
        {
            query.ShouldBe("Game");
            searched++;
            return new ProviderGameSearchResultDto([new("42", "Game", "https://example.com/game")], completeness, 500);
        });

        using var response = await host.Client.GetAsync($"/api/metadata-providers/{Provider}/games?query=Game");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("completeness").GetString().ShouldBe(completeness.ToString());
        body.RootElement.GetProperty("limit").GetInt32().ShouldBe(500);
        body.RootElement.GetProperty("items").EnumerateArray().ShouldHaveSingleItem().GetProperty("id").GetString().ShouldBe("42");
        searched.ShouldBe(1);
        resolved.ShouldBe(0);
    }

    [Theory]
    [InlineData("resolve=true")]
    [InlineData("cursor=bogus")]
    [InlineData("limit=1")]
    public async Task Search_UnsupportedContinuationOrRetiredResolveFlag_Is400BeforeProviderIo(string extra)
    {
        var searched = 0;
        await using var host = WithProvider(search: _ =>
        {
            searched++;
            return new ProviderGameSearchResultDto([], ProviderGameSearchCompleteness.ProviderLimited, 100);
        });

        using var response = await host.Client.GetAsync($"/api/metadata-providers/{Provider}/games?query=Game&{extra}");

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").TryGetProperty(extra.Split('=')[0], out _).ShouldBeTrue();
        searched.ShouldBe(0);
    }

    [Theory]
    [InlineData("games", "query", "   ")]
    [InlineData("games", "query", "Game\nwhere id=1;")]
    [InlineData("game-resolution", "input", "   ")]
    public async Task SearchAndResolve_InvalidInput_IsFieldKeyedBeforeProviderIo(string route, string field, string value)
    {
        var calls = 0;
        await using var host = WithProvider(resolve: _ =>
        {
            calls++;
            return ProviderMatchErrors.NotFound;
        }, search: _ =>
        {
            calls++;
            return new ProviderGameSearchResultDto([], ProviderGameSearchCompleteness.ProviderLimited, 100);
        });

        using var response = await host.Client.GetAsync($"/api/metadata-providers/{Provider}/{route}?{field}={Uri.EscapeDataString(value)}");

        using var problem = await ProblemEnvelope.ReadAsync(response, 400,
            route == "games" ? "ProviderMatch.InvalidQuery" : "ProviderMatch.InvalidInput");
        problem.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAndResolve_AnonymousCannotUseEitherProviderOperation()
    {
        using var client = fixture.CreateClient();
        (await client.GetAsync("/api/metadata-providers/igdb/games?query=Game")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/metadata-providers/igdb/game-resolution?input=42")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?query=42")]
    [InlineData("?input=42&cursor=bogus")]
    [InlineData("?input=42&resolve=true")]
    public async Task Resolution_MissingInputOrUnknownQuery_Is400BeforeProviderIo(string suffix)
    {
        var calls = 0;
        await using var host = WithProvider(resolve: id =>
        {
            calls++;
            return new ProviderGameDto(id, "Game", "https://example.com/game");
        });

        using var response = await host.Client.GetAsync($"/api/metadata-providers/{Provider}/game-resolution{suffix}");

        using (await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter"))
        { }
        calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("games", "query")]
    [InlineData("game-resolution", "input")]
    public async Task ProviderLookup_RepeatedScalarParameter_Is400BeforeProviderIo(string route, string field)
    {
        var calls = 0;
        await using var host = WithProvider(resolve: _ =>
        {
            calls++;
            return ProviderMatchErrors.NotFound;
        }, search: _ =>
        {
            calls++;
            return new ProviderGameSearchResultDto([], ProviderGameSearchCompleteness.ProviderLimited, 100);
        });

        using var response = await host.Client.GetAsync($"/api/metadata-providers/{Provider}/{route}?{field}=Game&{field}=Other");

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("GET", "/api/provider-matches/titles/{titleId}")]
    [InlineData("PUT", "/api/provider-matches/titles/{titleId}/igdb")]
    [InlineData("POST", "/api/provider-matches/titles/{titleId}/igdb/confirm")]
    [InlineData("POST", "/api/provider-matches/titles/{titleId}/igdb/unlink")]
    [InlineData("POST", "/api/provider-matches/igdb/search")]
    [InlineData("POST", "/api/metadata-providers/igdb/search")]
    public async Task RetiredRoutes_AnswerOnlyTheRoutingProblem(string method, string template)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method),
            template.Replace("{titleId}", IdCoder.Encode(1)))
        {
            Content = method == "GET" ? null : JsonContent.Create(new { })
        });

        ((int)response.StatusCode).ShouldBeOneOf(404, 405);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("errorCode").GetString()
            .ShouldBeOneOf("Request.RouteNotFound", "Request.MethodNotAllowed");
    }

    /// <summary>
    ///     A host with one provider, enabled and configured unless <paramref name="availability" /> says otherwise,
    ///     that resolves any id to a game unless <paramref name="resolve" /> answers instead.
    /// </summary>
    private ProviderHost WithProvider(ProviderAvailability? availability = null,
        Func<string, ErrorOr<ProviderGameDto>>? resolve = null,
        Func<string, ErrorOr<ProviderGameSearchResultDto>>? search = null)
    {
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns(Provider);
        adapter.Name.Returns("Fake provider");
        adapter.Capabilities.Returns(["artwork"]);
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(availability ?? new ProviderAvailability(true, true));
        resolve ??= id => new ProviderGameDto(id, "Game", "https://example.com/game");
        adapter.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult(resolve(call.Arg<string>())));
        search ??= _ => new ProviderGameSearchResultDto([], ProviderGameSearchCompleteness.ProviderLimited, 100);
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult(search(call.Arg<string>())));
        var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(adapter)));
        var client = factory.CreateClient();
        using var authenticated = fixture.CreateAuthenticatedClient();
        foreach (var header in authenticated.DefaultRequestHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        return new ProviderHost(factory, client);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? ifMatch, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    private static async Task<string> ETagAsync(HttpClient client, string titleId)
    {
        using var response = await client.GetAsync($"/api/titles/{titleId}/provider-matches/{Provider}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private async Task<Guid> StoredTitleRevisionAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Titles
            .Where(title => title.Id == id).Select(title => title.Revision).SingleAsync();
    }

    private async Task<string> SeedTitleAsync(string? linkedGameId = null)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Provider match test {Guid.NewGuid():N}";
        var title = new Romd.Persistence.Entities.TitleEntity
        {
            PlatformId = await db.Platforms.Select(x => x.Id).FirstAsync(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        if (linkedGameId is not null)
        {
            db.TitleExternalIds.Add(Romd.Persistence.Entities.TitleExternalIdEntity.FromDomain(
                Romd.Domain.Catalog.TitleExternalId.CreateNew(title.Id, Provider, linkedGameId, 0.5f, createdAt: DateTimeOffset.UnixEpoch)));
            await db.SaveChangesAsync();
        }

        return IdCoder.Encode(title.Id);
    }

    private sealed record ProviderHost(WebApplicationFactory<Romd.Admin.Host.Program> Factory, HttpClient Client)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
