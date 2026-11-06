using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class IgdbProviderCompositionTests
{
    [Fact]
    public async Task ArtworkAcrossTitleScopes_UsesConfiguredResilienceAndOneSharedAuthenticationToken()
    {
        var handler = new IgdbHandler();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddRomdAdminCurationServices();
        // Use deployment credentials; no settings database is needed for this transport test.
        services.RemoveAll<IgdbProviderSettingsService>();
        services.AddScoped(_ => Substitute.For<IPlatformAliasRepository>());
        services.AddSingleton(Substitute.For<IProviderRateLimiter>());
        services.Configure<IgdbProviderOptions>(options =>
        {
            options.ClientId = "composition-client";
            options.ClientSecret = "composition-secret";
        });
        services.AddHttpClient(nameof(IgdbMetadataProvider))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => new UnexpectedDefaultClient());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        for (int title = 0; title < 2; title++)
        {
            await using var scope = provider.CreateAsyncScope();
            var artwork = scope.ServiceProvider.GetRequiredService<IgdbArtworkProvider>();

            var result = await artwork.GetCandidatesAsync("42", new ProviderArtworkQuery(ArtworkRole.Poster));

            result.IsError.ShouldBeFalse();
            result.Value.Items.ShouldHaveSingleItem().ProviderAssetId.ShouldBe("cover1");
        }

        handler.TokenRequests.ShouldBe(1, "authentication belongs to the host, independently of title database scopes");
        handler.GameRequests.ShouldBe(3, "the configured named client retries the first rate-limited request");
    }

    private sealed class UnexpectedDefaultClient : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("IGDB must use its configured named HTTP client.");
    }

    private sealed class IgdbHandler : HttpMessageHandler
    {
        public int TokenRequests { get; private set; }
        public int GameRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                TokenRequests++;
                return Task.FromResult(Json("""{"access_token":"composition-token","expires_in":3600,"token_type":"bearer"}"""));
            }

            request.RequestUri.Host.ShouldBe("api.igdb.com");
            request.Headers.Authorization.ShouldBe(new AuthenticationHeaderValue("Bearer", "composition-token"));
            GameRequests++;
            if (GameRequests == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }
            return Task.FromResult(Json("""[{"id":42,"slug":"test-game","cover":{"image_id":"cover1","width":600,"height":900}}]"""));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
