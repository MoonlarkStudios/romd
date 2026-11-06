using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Romd.Infrastructure.Resilience;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Resilience;

public sealed class ResilienceConfigurationTests
{
    [Fact]
    public async Task AddIgdbResilienceHandler_ServerError_RetriesRequest()
    {
        var requestCount = 0;
        var services = new ServiceCollection();

        services.AddHttpClient("igdb")
            .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(_ =>
            {
                var count = Interlocked.Increment(ref requestCount);
                return new HttpResponseMessage(count == 1
                    ? HttpStatusCode.InternalServerError
                    : HttpStatusCode.OK);
            }))
            .AddIgdbResilienceHandler();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("igdb");

        using var response = await client.PostAsync(
            "https://api.igdb.com/v4/games",
            new StringContent("fields id;"),
            CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        requestCount.ShouldBe(2);
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
