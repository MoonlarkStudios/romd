using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Romd.Admin.Application.Artwork.Providers;
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
    public async Task Backdrop_UnsupportedRole_ReturnsValidationErrorsWithoutTransport()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("No transport expected"));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);
        (await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Backdrop))).IsError.ShouldBeTrue();
        (await provider.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Backdrop, AssetUrl)).IsError.ShouldBeTrue();
        (await provider.DownloadPreviewAsync("steamgriddb", ArtworkRole.Backdrop, AssetUrl)).IsError.ShouldBeTrue();
        handler.Requests.ShouldBeEmpty();
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
    public async Task GetCandidatesAsync_StaticFilter_MapsProvenanceAndOpaqueAdapterPaging()
    {
        var handler = new Handler(_ => Page(total: 100));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster, "600x900", "alternate"));

        result.IsError.ShouldBeFalse();
        result.Value.NextPage.ShouldBe(1);
        var candidate = result.Value.Items.Single();
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
    public async Task GetCandidatesAsync_UnsafeProviderAsset_Rejects(string location)
    {
        var handler = new Handler(_ => Page(location));
        using var provider = new SteamGridDbArtworkProvider(new Credentials(), handler);

        var result = await provider.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster));

        result.FirstError.Code.ShouldBe("Artwork.ProviderInvalidResponse");
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

    private static HttpResponseMessage Page(string location = AssetUrl, int total = 1,
        string preview = "https://cdn2.steamgriddb.com/thumb/abc.jpg", int width = 600, int height = 900,
        object? style = null) => Json(new
    {
        success = true, total, page = 0, limit = 50,
        data = new[] { new { id = 7, width, height, style = style ?? "alternate", url = location,
            thumb = preview, author = new { name = "Artist" } } }
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
