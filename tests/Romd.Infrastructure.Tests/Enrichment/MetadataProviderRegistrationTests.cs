using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class MetadataProviderRegistrationTests
{
    [Theory]
    [InlineData("worker")]
    [InlineData("admin,worker")]
    [InlineData("worker,admin")]
    [InlineData("admin,admin")]
    [InlineData("worker,worker")]
    [InlineData("admin,worker,admin,worker")]
    public async Task CurationComposition_RepeatedRegistration_PreservesOneProviderRetryBudget(string composition)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (string surface in composition.Split(','))
        {
            if (surface == "admin")
                services.AddRomdAdminCurationServices();
            else
                services.AddRomdWorkerCurationServices();
        }
        int sends = 0;
        services.AddHttpClient(nameof(IgdbMetadataProvider)).ConfigurePrimaryHttpMessageHandler(() =>
            new RateLimitedHandler(() => Interlocked.Increment(ref sends)));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IgdbMetadataProvider));

        using var response = await client.GetAsync("https://local-fake.test/igdb");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        sends.ShouldBe(4, "one request and three retries must not become nested retry budgets");
        services.Count(descriptor => descriptor.ServiceType == typeof(IMetadataProvider)).ShouldBe(1);
        services.Count(descriptor => descriptor.ServiceType == typeof(IProviderIdentityAdapter)).ShouldBe(2);
    }

    private sealed class RateLimitedHandler(Action sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent();
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }
}
