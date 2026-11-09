using System.Net.Http.Json;
using ErrorOr;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.PrepareReleaseDownload;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Ticket values never appear in logs or diagnostics (docs/decisions/api-contract-policy.md,
///     "Protected By Default"). Redeeming a capability token on either host writes one request log
///     whose path is redacted, and no event the host writes carries the token: not the request
///     log, ASP.NET Core's request starting/finished events, nor the hosting log scope that every
///     in-request event carries.
/// </summary>
public sealed class CapabilityTokenLoggingTests : IDisposable
{
    private const string TestJwtSecret = "capability-token-logging-secret-at-least-32-characters";
    private const string Token = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"romd-capability-token-logging-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("/delivery/content", "/delivery/content")]
    [InlineData("/delivery/bios", "/delivery/bios")]
    [InlineData("/delivery/original-layout", "/delivery/original-layout")]
    [InlineData("/delivery/disc-set", "/delivery/disc-set")]
    [InlineData("/Delivery/Content", "/delivery/content")]
    public async Task ConsumerRedemption_LogsRedactedPathAndNoToken(string route, string redactedRoute)
    {
        var sink = new CapturingLogSink();
        await using var factory = RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(
            _dataDirectory,
            "Testing",
            TestJwtSecret,
            services => CapturingLogSink.Register(services, sink),
            new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel:Default"] = "Verbose",
                ["Romd:ConsumerDelivery:SigningKeyId"] = "logging-test",
                ["Romd:ConsumerDelivery:SigningSecret"] = "capability-token-logging-delivery-signing-secret"
            });

        using var response = await RequestAsync(factory, $"{route}/{Token}");

        ((int)response.StatusCode).ShouldBe(401);
        await AssertRequestLoggedWithoutAsync(sink, $"{redactedRoute}/[redacted]", Token);
    }

    [Fact]
    public async Task ConsumerCapabilityEndpointInventory_LogsNoToken()
    {
        var sink = new CapturingLogSink();
        await using var factory = RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(
            _dataDirectory,
            "Testing",
            TestJwtSecret,
            services => CapturingLogSink.Register(services, sink),
            new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel:Default"] = "Verbose",
                ["Romd:ConsumerDelivery:SigningKeyId"] = "logging-test",
                ["Romd:ConsumerDelivery:SigningSecret"] = "capability-token-logging-delivery-signing-secret"
            });
        using var client = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.Parameters.Any(parameter => parameter.Name == "token"))
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToArray();
        routes.ShouldNotBeEmpty();

        foreach (var route in routes)
        {
            using var response = await client.GetAsync(route.Replace("{token}", Token, StringComparison.Ordinal));
            ((int)response.StatusCode).ShouldBe(401);
            var redactedPath = route.Replace("{token}", "[redacted]", StringComparison.Ordinal);
            await sink.WaitForSingleAsync(logEvent =>
                logEvent.Properties.TryGetValue("SourceContext", out var source)
                && source is ScalarValue { Value: CapturingLogSink.RequestLoggingSource }
                && logEvent.Properties.TryGetValue("RequestPath", out var path)
                && path is ScalarValue { Value: string value } && value == redactedPath);
            sink.Events.Select(Describe).ShouldAllBe(description => !description.Contains(Token, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AdminDownloadRedemption_LogsRedactedPathAndNoToken()
    {
        var sink = new CapturingLogSink();
        await using var factory = CreateAdmin(sink);

        using var response = await RequestAsync(factory, $"/api/download-tickets/{Token}");

        ((int)response.StatusCode).ShouldBe(404);
        await AssertRequestLoggedWithoutAsync(sink, "/api/download-tickets/[redacted]", Token);
    }

    /// <summary>The issued ticket travels in the 201's Location and body, which no event the host writes carries.</summary>
    [Fact]
    public async Task AdminTicketIssuance_LogsNoToken()
    {
        var sink = new CapturingLogSink();
        var releases = Substitute.For<IQueryHandler<PrepareReleaseDownloadQuery, AdminDownloadTicket>>();
        releases.HandleAsync(default!, default).ReturnsForAnyArgs(Task.FromResult<ErrorOr<AdminDownloadTicket>>(
            new AdminDownloadTicket($"/api/download-tickets/{Token}", DateTimeOffset.UtcNow)));
        await using var factory = RomdHostTestFactory.CreateAdmin(
            _dataDirectory,
            TestJwtSecret,
            services =>
            {
                CapturingLogSink.Register(services, sink);
                services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                services.AddSingleton(releases);
            },
            new Dictionary<string, string> { ["Serilog:MinimumLevel:Default"] = "Verbose" });
        using var client = factory.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);

        using var response = await client.PostAsJsonAsync("/api/download-tickets",
            new { target = "Release", id = IdCoder.Encode(1) });

        ((int)response.StatusCode).ShouldBe(201);
        response.Headers.Location!.OriginalString.ShouldContain(Token);
        await AssertRequestLoggedWithoutAsync(sink, "/api/download-tickets", Token);
    }

    /// <summary>SignalR clients send the bearer token as a query parameter on hub connections.</summary>
    [Fact]
    public async Task AdminHubQueryStringToken_IsNotLogged()
    {
        var sink = new CapturingLogSink();
        var tokens = Substitute.For<IOpenIddictTokenManager>();
        tokens.FindByReferenceIdAsync(Token, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<object?>(null));
        await using var factory = CreateAdmin(sink, services => services.AddSingleton(tokens));

        using var response = await RequestAsync(factory, $"/hubs/jobs?id=connection&access_token={Token}");

        ((int)response.StatusCode).ShouldBe(401);
        await tokens.Received(1).FindByReferenceIdAsync(Token, Arg.Any<CancellationToken>());
        await AssertRequestLoggedWithoutAsync(sink, "/hubs/jobs", Token);
    }

    [Fact]
    public async Task AdminRedemption_UsesHostLoggerWhenGlobalLoggerChanges()
    {
        var sink = new CapturingLogSink();
        await using var factory = CreateAdmin(sink);
        using var client = factory.CreateClient();
        var previous = Serilog.Log.Logger;
        using var unrelatedLogger = new Serilog.LoggerConfiguration().CreateLogger();
        Serilog.Log.Logger = unrelatedLogger;
        try
        {
            using var response = await client.GetAsync($"/api/download-tickets/{Token}");
            ((int)response.StatusCode).ShouldBe(404);
            await AssertRequestLoggedWithoutAsync(sink, "/api/download-tickets/[redacted]", Token);
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }
    }

    private WebApplicationFactory<Romd.Admin.Host.Program> CreateAdmin(
        CapturingLogSink sink, Action<IServiceCollection>? configureServices = null) =>
        RomdHostTestFactory.CreateAdmin(
            _dataDirectory,
            TestJwtSecret,
            services =>
            {
                CapturingLogSink.Register(services, sink);
                configureServices?.Invoke(services);
            },
            new Dictionary<string, string> { ["Serilog:MinimumLevel:Default"] = "Verbose" });

    private static async Task<HttpResponseMessage> RequestAsync<TProgram>(
        WebApplicationFactory<TProgram> factory,
        string requestUri)
        where TProgram : class
    {
        using var client = factory.CreateClient();
        return await client.GetAsync(requestUri);
    }

    private static async Task AssertRequestLoggedWithoutAsync(CapturingLogSink sink, string loggedPath, string secret)
    {
        var requestLog = await sink.WaitForRequestLogAsync();
        requestLog.Properties["RequestPath"].ShouldBe(new ScalarValue(loggedPath));
        sink.Events
            .Select(Describe)
            .Where(description => description.Contains(secret, StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    private static string Describe(LogEvent logEvent) => string.Join(
        Environment.NewLine,
        [
            logEvent.RenderMessage(),
            .. logEvent.Properties.Select(property => $"{property.Key}={property.Value}"),
            logEvent.Exception?.ToString() ?? string.Empty
        ]);
}
