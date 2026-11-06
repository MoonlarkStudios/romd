using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Artwork.SteamGridDb;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class SteamGridDbArtworkProviderTests
{
    private const string AssetUrl = "https://cdn2.steamgriddb.com/grid/abc.png";
    private const string LogoUrl = "https://cdn2.steamgriddb.com/logo/logo.png";

    [Fact]
    public async Task GetCandidatesAsync_Backdrop_UsesHeroesAndPreservesBackdropRole()
    {
        var handler = new Handler(_ => Page("https://cdn2.steamgriddb.com/hero/abc.png",
            preview: "https://cdn2.steamgriddb.com/hero_thumb/abc.jpg", width: 1920, height: 620));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Backdrop,
            "1920x620", "alternate", MediaType: MediaType.Background));

        result.IsError.ShouldBeFalse();
        result.Value.Items.Single().Role.ShouldBe(ArtworkRole.Backdrop);
        result.Value.Items.Single().SourcePageUrl.ShouldBe("https://www.steamgriddb.com/hero/7");
        handler.Requests.Single().Uri.AbsoluteUri.ShouldContain("/heroes/game/42?");
        handler.Requests.Single().Uri.Query.ShouldContain("dimensions=1920x620");
    }

    [Fact]
    public async Task Backdrop_DownloadsHeroOriginalAndPreview_RejectsGridPaths()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(null), handler);

        var original = await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Backdrop,
            "https://cdn2.steamgriddb.com/hero/abc.png");
        var preview = await provider.DownloadPreviewAsync("steamgriddb", ArtworkRole.Backdrop,
            "https://cdn2.steamgriddb.com/hero_thumb/abc.jpg");

        original.IsError.ShouldBeFalse();
        original.Value.SourcePageUrl.ShouldBe("https://www.steamgriddb.com/hero/7");
        preview.IsError.ShouldBeFalse();
        (await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Backdrop, AssetUrl)).IsError.ShouldBeTrue();
        (await provider.DownloadPreviewAsync("steamgriddb", ArtworkRole.Backdrop, AssetUrl)).IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(2);
        handler.Requests.ShouldAllBe(request => request.Authorization == null);
    }

    [Fact]
    public async Task SearchAsync_Query_EncodesPathAndKeepsCredentialsServerSide()
    {
        var handler = new Handler(_ => Json(new { success = true, data = new[] { new { id = 42, name = "Test Game" } } }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.SearchAsync("Game / Test?");

        result.IsError.ShouldBeFalse();
        result.Value.Single().Id.ShouldBe("42");
        handler.Requests.Single().Uri.AbsoluteUri.ShouldContain("Game%20%2F%20Test%3F");
        handler.Requests.Single().Authorization.ShouldBe("Bearer secret-test-key");
        provider.Capabilities.SupportsLanguageFilter.ShouldBeFalse();
    }

    [Fact]
    public async Task SearchAsync_OverRecordBudget_RejectsInsteadOfTruncating()
    {
        var handler = new Handler(_ => Json(new
        {
            success = true,
            data = Enumerable.Range(1, 101).Select(id => new { id, name = $"Game {id}" }).ToArray()
        }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.SearchAsync("Game");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAsync_InvalidRow_RejectsInsteadOfOmittingEvidence()
    {
        var handler = new Handler(_ => Json(new
        {
            success = true,
            data = new[] { new { id = 1, name = "Game" }, new { id = 2, name = "" } }
        }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.SearchAsync("Game");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public async Task IdentitySearch_AutocompleteNeverPromisesExhaustion(int count)
    {
        var handler = new Handler(_ => Json(new
        {
            success = true,
            data = Enumerable.Range(1, count).Select(id => new { id, name = $"Game {id}" }).ToArray()
        }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);
        var adapter = new SteamGridDbIdentityAdapter(provider, Substitute.For<ISteamGridDbSettingsService>());

        var result = await adapter.SearchAsync("Game", default);

        result.IsError.ShouldBeFalse();
        result.Value.Items.Count.ShouldBe(count);
        result.Value.Completeness.ShouldBe(ProviderGameSearchCompleteness.ProviderLimited);
        result.Value.Limit.ShouldBe(100);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task IdentityResolution_AfterLimitedSearch_StillResolvesExplicitIdAndTrustedSteamBridge()
    {
        var handler = new Handler(request => Json(new
        {
            success = true,
            data = request.RequestUri!.AbsolutePath.Contains("/search/", StringComparison.Ordinal)
                ? (object)Array.Empty<object>() : new { id = 42, name = "Game", release_date = (long?)null }
        }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);
        var adapter = new SteamGridDbIdentityAdapter(provider, Substitute.For<ISteamGridDbSettingsService>());

        var suggestions = await adapter.SearchAsync("Game", default);
        var resolved = await adapter.ResolveAsync("https://www.steamgriddb.com/game/42", default);
        var bridge = await provider.ResolveSteamIdentityAsync("220", default);

        suggestions.Value.Completeness.ShouldBe(ProviderGameSearchCompleteness.ProviderLimited);
        resolved.Value.Id.ShouldBe("42");
        bridge.Value.Id.ShouldBe("42");
        handler.Requests.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("\"1994\"")]
    [InlineData("9223372036854775807")]
    public async Task Identity_MalformedPresentReleaseDate_FailsSearchResolveAndBridge(string date)
    {
        var handler = new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true,\"data\":" +
                (request.RequestUri!.AbsolutePath.Contains("/search/", StringComparison.Ordinal) ? "[" : "") +
                "{\"id\":42,\"name\":\"Game\",\"release_date\":" + date + "}" +
                (request.RequestUri.AbsolutePath.Contains("/search/", StringComparison.Ordinal) ? "]}" : "}"), Encoding.UTF8, "application/json")
        });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        (await provider.SearchAsync("Game")).FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        (await provider.ResolveIdentityAsync("42", default)).FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        (await provider.ResolveSteamIdentityAsync("220", default)).FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("Game\nName")]
    [InlineData(null)]
    public async Task IdentityResolution_MalformedName_RejectsExactIdAndBridge(string? input)
    {
        var name = input ?? new string('X', 501);
        var handler = new Handler(_ => Json(new { success = true, data = new { id = 42, name } }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        (await provider.ResolveIdentityAsync("42", default)).FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        (await provider.ResolveSteamIdentityAsync("220", default)).FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task GetCandidatesAsync_StaticFilter_MapsProvenanceAndOpaqueAdapterPaging()
    {
        var handler = new Handler(_ => Page(total: 100));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster, "600x900", "alternate"));

        result.IsError.ShouldBeFalse();
        result.Value.NextPage.ShouldBe(1);
        result.Value.Items.Count.ShouldBe(50);
        var candidate = result.Value.Items[0];
        candidate.AssetUrl.AbsoluteUri.ShouldBe(AssetUrl);
        candidate.Attribution.ShouldBe("Artist");
        candidate.SourcePageUrl.ShouldBe("https://www.steamgriddb.com/grid/7");
        var uri = handler.Requests.Single().Uri.AbsoluteUri;
        uri.ShouldContain("types=static");
        uri.ShouldContain("dimensions=600x900");
        uri.ShouldContain("styles=alternate");
    }

    [Fact]
    public async Task GetCandidatesAsync_Logo_UsesDedicatedProviderCapabilityAndMediaClassification()
    {
        var handler = new Handler(_ => Page(LogoUrl, preview: "https://cdn2.steamgriddb.com/logo_thumb/logo.png",
            width: 1200, height: 320, style: new[] { "official" }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var capability = provider.Capabilities.Roles.Single(role => role.Role == ArtworkRole.Logo);
        capability.Dimensions.ShouldBeEmpty();
        capability.Styles.ShouldContain("official");
        provider.Capabilities.MediaTypes!.ShouldContain(MediaType.Logo);
        var result = await provider.GetCandidatesAsync("42",
            new ProviderArtworkQuery(ArtworkRole.Logo, Style: "official", MediaType: MediaType.Logo));

        result.IsError.ShouldBeFalse();
        var candidate = result.Value.Items.Single();
        candidate.Role.ShouldBe(ArtworkRole.Logo);
        candidate.AssetUrl.AbsoluteUri.ShouldBe(LogoUrl);
        candidate.SourcePageUrl.ShouldBe("https://www.steamgriddb.com/logo/7");
        var uri = handler.Requests.Single().Uri.AbsoluteUri;
        uri.ShouldContain("/logos/game/42?");
        uri.ShouldContain("styles=official");
        uri.ShouldNotContain("dimensions=");
    }

    [Theory]
    [InlineData(ArtworkRole.Poster, "image/png,image/jpeg,image/webp")]
    [InlineData(ArtworkRole.Hero, "image/png,image/jpeg,image/webp")]
    [InlineData(ArtworkRole.Backdrop, "image/png,image/jpeg,image/webp")]
    [InlineData(ArtworkRole.Logo, "image/png,image/webp")]
    public async Task GetCandidatesAsync_Role_RequestsOnlyProviderSupportedMimeTypes(ArtworkRole role, string expectedMimes)
    {
        var handler = new Handler(request =>
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            return query["mimes"].ToString() == expectedMimes
                ? Json(new { success = true, data = Array.Empty<object>() })
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("38157", new ProviderArtworkQuery(role));

        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldBeEmpty();
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("https://127.0.0.1/grid/abc.png")]
    [InlineData("http://cdn2.steamgriddb.com/grid/abc.png")]
    [InlineData("https://cdn2.steamgriddb.com.attacker.invalid/grid/abc.png")]
    [InlineData("https://user@cdn2.steamgriddb.com/grid/abc.png")]
    [InlineData("https://cdn2.steamgriddb.com:444/grid/abc.png")]
    [InlineData("https://cdn2.steamgriddb.com/grid/abc.svg")]
    public async Task GetCandidatesAsync_UnsafeProviderAsset_SkipsCandidate(string location)
    {
        var handler = new Handler(_ => Page(location));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster));

        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldBeEmpty();
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DownloadAsync_TrustedCandidate_DownloadsWithoutCredentialsOrProviderApi()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(null), handler);

        var result = await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Poster, AssetUrl);

        result.IsError.ShouldBeFalse();
        result.Value.Bytes.ShouldBe([1, 2, 3]);
        handler.Requests.Single().Authorization.ShouldBeNull();
        handler.Requests.Single().Uri.Host.ShouldBe("cdn2.steamgriddb.com");
    }

    [Fact]
    public async Task DownloadAsync_Logo_AllowsOnlyDedicatedLogoPath()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(null), handler);

        var result = await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Logo, LogoUrl);
        var wrongRole = await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Hero, LogoUrl);

        result.IsError.ShouldBeFalse();
        result.Value.SourcePageUrl.ShouldBe("https://www.steamgriddb.com/logo/7");
        wrongRole.FirstError.Code.ShouldBe("Artwork.ProviderInvalidRequest");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DownloadAsync_RedirectOutsideAllowlist_RejectsBeforeSecondRequest()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("http://169.254.169.254/latest/meta-data") } });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Poster, AssetUrl);

        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAsync_ApiRedirect_RejectsWithoutForwardingCredentials()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://cdn2.steamgriddb.com/grid/a.png") } });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.SearchAsync("Game");

        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAsync_RateLimit_SanitizesProviderErrorAndHonorsCooldown()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)) },
            Content = new StringContent("secret provider diagnostic")
        });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var first = await provider.SearchAsync("Game");
        var second = await provider.SearchAsync("Game");

        first.FirstError.Code.ShouldBe("Artwork.ProviderRateLimited");
        second.FirstError.Code.ShouldBe("Artwork.ProviderRateLimited");
        first.FirstError.Description.ShouldNotContain("secret");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAsync_SharedRateGate_AppliesAcrossProviderScopes()
    {
        var gate = new SteamGridDbRateGate();
        var firstHandler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var nextHandler = new Handler(_ => throw new InvalidOperationException("Cooldown should prevent transport."));
        using var first = new SteamGridDbArtworkProvider(new Credentials(), firstHandler, gate);
        using var next = new SteamGridDbArtworkProvider(new Credentials(), nextHandler, gate);

        (await first.SearchAsync("Game")).FirstError.Code.ShouldBe("Artwork.ProviderRateLimited");
        (await next.SearchAsync("Game")).FirstError.Code.ShouldBe("Artwork.ProviderRateLimited");

        nextHandler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DownloadPreviewAsync_ThumbnailOnly_DoesNotPermitOriginalLocation()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.DownloadPreviewAsync("steamgriddb", ArtworkRole.Hero,
            "https://cdn2.steamgriddb.com/hero_thumb/a.jpg");
        var invalid = await provider.DownloadPreviewAsync("steamgriddb", ArtworkRole.Poster, AssetUrl);

        result.IsError.ShouldBeFalse();
        invalid.FirstError.Code.ShouldBe("Artwork.ProviderInvalidRequest");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadAsync_OversizedDeclaredBody_RejectsWithoutReadingBody()
    {
        var handler = new Handler(_ =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 32 * 1024 * 1024 + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Poster, AssetUrl);

        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task SearchAsync_MissingCredentials_MakesNoRequests()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var provider = new SteamGridDbArtworkProvider(new Credentials(null), handler);

        var result = await provider.SearchAsync("Game");

        result.FirstError.Code.ShouldBe("Artwork.ProviderNotConfigured");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_Cancelled_PropagatesCancellation()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => provider.SearchAsync("Game", cancellation.Token));
    }

    [Fact]
    public async Task ResolveSteamIdentity_UsesExternalIdEndpointAndValidatesGameShape()
    {
        var handler = new Handler(_ => Json(new { success = true, data = new { id = 42, name = "Game" } }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);
        var result = await provider.ResolveSteamIdentityAsync("220", default);
        result.IsError.ShouldBeFalse();
        result.Value.Id.ShouldBe("42");
        result.Value.Year.ShouldBeNull();
        handler.Requests.Single().Uri.AbsolutePath.ShouldBe("/api/v2/games/steam/220");
    }

    public static TheoryData<string> UnexpectedIdentityShapes => new()
    {
        """{"success":true,"data":{"id":42,"name":""}}""",
        """{"success":true,"data":{"id":43,"name":"Another game"}}""",
        """{"success":true}""",
        """{"success":true,"data":"""
    };

    [Theory]
    [MemberData(nameof(UnexpectedIdentityShapes))]
    public async Task ResolveIdentity_UnexpectedShapeOrInvalidJson_IsAnInvalidProviderResponse(string body)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var byId = await provider.ResolveIdentityAsync("42", default);
        var bySteam = await provider.ResolveSteamIdentityAsync("220", default);

        byId.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
        if (body.Contains("Another game", StringComparison.Ordinal))
            return;
        bySteam.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task ResolveIdentity_NoSuchGame_IsNotFound()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.ResolveIdentityAsync("42", default);

        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound);
    }

    [Fact]
    public async Task SearchAsync_NullReleaseDate_RemainsUnknown()
    {
        var handler = new Handler(_ => Json(new { success = true, data = new[] { new { id = 42, name = "Game", release_date = (long?)null } } }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);
        var result = await provider.SearchAsync("Game");
        result.IsError.ShouldBeFalse();
        result.Value.Single().Year.ShouldBeNull();
    }

    [Fact]
    public async Task GetCandidatesAsync_InvalidRows_PreservesValidRowsAndRawPaging()
    {
        var rows = Enumerable.Range(0, 50).Select(index => (object)new
        {
            id = index + 1,
            width = 600,
            height = 900,
            url = index == 25 ? AssetUrl : "https://attacker.invalid/grid/private.png?secret=value",
            thumb = "https://cdn2.steamgriddb.com/thumb/a.jpg"
        }).ToArray();
        rows[0] = new { id = 0 };
        rows[1] = "invalid row";
        var handler = new Handler(_ => Json(new { success = true, data = rows }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster));

        result.IsError.ShouldBeFalse();
        result.Value.Items.Single().ProviderAssetId.ShouldBe("26");
        result.Value.NextPage.ShouldBe(1);
    }

    [Theory]
    [InlineData("{\"success\":true,\"data\":{}}")]
    [InlineData("{\"success\":true}")]
    [InlineData("{\"success\":true,\"data\":[],\"total\":-1}")]
    [InlineData("{\"success\":true,\"data\":[],\"total\":\"invalid\"}")]
    public async Task GetCandidatesAsync_MalformedEnvelope_Rejects(string json)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster));

        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
    }

    [Fact]
    public async Task SearchAsync_RetryAfterDate_UsesInjectedClockAndPublishesCooldown()
    {
        var clock = new Clock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var retryAt = clock.GetUtcNow().AddMinutes(2);
        var gate = new SteamGridDbRateGate(clock);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Headers = { RetryAfter = new RetryConditionHeaderValue(retryAt) } });
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler, gate);

        var first = await provider.SearchAsync("Game");
        clock.Now = clock.Now.AddSeconds(30);
        var second = await provider.SearchAsync("Game");

        first.FirstError.Metadata!["RetryAfterUtc"].ShouldBe(retryAt);
        second.FirstError.Metadata!["RetryAfterUtc"].ShouldBe(retryAt);
        handler.Requests.Count.ShouldBe(1);
        clock.Now = retryAt;
        gate.IsLimited.ShouldBeFalse();
        await provider.SearchAsync("Game");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SearchAsync_QueuedRequest_RechecksSharedCooldownBeforeSending()
    {
        var requests = new SteamGridDbRequestGate(1);
        var rate = new SteamGridDbRateGate();
        using var lease = await requests.EnterAsync(default);
        var handler = new Handler(_ => throw new InvalidOperationException("Cooldown should prevent transport."));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler, rate, requests);
        var pending = provider.SearchAsync("Game");
        pending.IsCompleted.ShouldBeFalse();

        rate.Defer(TimeSpan.FromMinutes(1));
        lease.Dispose();
        var result = await pending;

        result.FirstError.Code.ShouldBe("Artwork.ProviderRateLimited");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_QueuedCancellation_DoesNotLeakPermit()
    {
        var requests = new SteamGridDbRequestGate(1);
        using var lease = await requests.EnterAsync(default);
        var handler = new Handler(_ => Json(new { success = true, data = Array.Empty<object>() }));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler, requestGate: requests);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.SearchAsync("Game", cancellation.Token);
        pending.IsCompleted.ShouldBeFalse();

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        lease.Dispose();
        (await provider.SearchAsync("Game")).IsError.ShouldBeFalse();
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DownloadAsync_SharedConcurrencyGate_HoldsPermitUntilBodyCompletes()
    {
        var requests = new SteamGridDbRequestGate(1);
        var stream = new BlockingReadStream();
        var firstHandler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var secondHandler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) });
        using var first = new SteamGridDbArtworkProvider(new Credentials(), firstHandler, requestGate: requests);
        using var second = new SteamGridDbArtworkProvider(new Credentials(), secondHandler, requestGate: requests);
        var firstDownload = first.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Poster, AssetUrl);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondDownload = second.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Poster, AssetUrl);
        try
        {
            secondDownload.IsCompleted.ShouldBeFalse();
            secondHandler.Requests.ShouldBeEmpty();
        }
        finally
        {
            stream.Release.TrySetResult();
        }

        (await firstDownload.WaitAsync(TimeSpan.FromSeconds(5))).IsError.ShouldBeFalse();
        (await secondDownload.WaitAsync(TimeSpan.FromSeconds(5))).IsError.ShouldBeFalse();
        secondHandler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DownloadAsync_TransportFailure_ReleasesConcurrencyPermit()
    {
        var requests = new SteamGridDbRequestGate(1);
        var handler = new Handler(_ => throw new HttpRequestException());
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler, requestGate: requests);

        (await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Poster, AssetUrl))
            .FirstError.Code.ShouldBe("Artwork.ProviderUnavailable");
        using var lease = await requests.EnterAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class BlockingReadStream() : MemoryStream([1])
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpResponseMessage Page(string location = AssetUrl, int total = 1,
        string preview = "https://cdn2.steamgriddb.com/thumb/abc.jpg", int width = 600, int height = 900,
        object? style = null) => Json(new
        {
            success = true,
            total,
            page = 0,
            limit = 50,
            data = Enumerable.Range(7, Math.Min(50, total)).Select(id => new
            {
                id,
                width,
                height,
                style = style ?? "alternate",
                url = location,
                thumb = preview,
                author = new { name = "Artist" }
            })
        });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Credentials(string? key = "secret-test-key") : ISteamGridDbCredentials
    {
        public Task<string?> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(key);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(response(request));
        }
    }
}
