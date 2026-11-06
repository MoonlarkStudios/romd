using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     The one systemKey binding seam, resolved by the declared-parameter stage
///     (docs/decisions/api-contract-policy.md, "Strict Validation" and "One Error Envelope"): an
///     unknown route key is a 404, an unknown query filter is a 400 naming the filter by its wire
///     name, a known key reaches the handler resolved, the stage reports its own failures together,
///     the endpoint declares the problem it answers, every route or query <c>systemKey</c> on either
///     host binds through the seam inside the stage, and both live OpenAPI documents publish every
///     bound key as a string.
/// </summary>
public sealed partial class SystemKeyParameterTests : IAsyncLifetime
{
    private const int SnesId = 17;

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = Build(api =>
        {
            api.MapGet("/systems/{systemKey}/things", (SystemKeyParameter systemKey) => systemKey.SystemId);
            api.MapGet("/systems/{systemKey}/pages", (SystemKeyParameter systemKey, [Range(1, 10)] int limit = 5) => limit);
            api.MapGet("/things", (SystemKeyParameter? systemKey, [Range(1, 10)] int limit = 5) => systemKey?.SystemId ?? 0);
            api.MapGet("/renamed", ([FromQuery(Name = "system")] SystemKeyParameter? key) => key?.SystemId ?? 0);
            api.MapGet("/systems/{systemKey}/related", (SystemKeyParameter systemKey,
                [FromQuery(Name = "relatedSystemKey")] SystemKeyParameter? related) => related?.SystemId ?? 0);
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/api/systems/snes/things")]
    [InlineData("/api/things?systemKey=snes")]
    [InlineData("/api/renamed?system=snes")]
    public async Task KnownKey_ReachesTheHandlerResolved(string path)
    {
        (await _client.GetStringAsync(path)).ShouldBe(SnesId.ToString());
    }

    [Fact]
    public async Task UnknownRouteKey_Returns404Envelope()
    {
        using var response = await _client.GetAsync("/api/systems/no-such-system/things");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Systems.NotFound");
    }

    [Theory]
    [InlineData("/api/things?systemKey=no-such-system", "systemKey")]
    [InlineData("/api/renamed?system=no-such-system", "system")]
    public async Task UnknownQueryFilter_Returns400NamingTheWireParameterWithoutEchoingIt(string path, string parameter)
    {
        using var response = await _client.GetAsync(path);

        (await response.Content.ReadAsStringAsync()).ShouldNotContain("no-such-system");
        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe([parameter]);
    }

    [Fact]
    public async Task UnknownQueryFilterAndOutOfRangeLimit_AreReportedTogether()
    {
        using var response = await _client.GetAsync("/api/things?systemKey=no-such-system&limit=50");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .ShouldBe(["limit", "systemKey"], ignoreOrder: true);
    }

    // Request-shape failures are reported before resource resolution: a 400 means the request is
    // invalid regardless of server state, so it outranks the unknown route key's 404.
    [Theory]
    [InlineData("/api/systems/no-such-system/pages?limit=50", "limit")]
    [InlineData("/api/systems/no-such-system/related?relatedSystemKey=no-such-system", "relatedSystemKey")]
    public async Task UnknownRouteKeyAndARequestShapeFailure_AnswerTheStateIndependent400(string path, string parameter)
    {
        using var response = await _client.GetAsync(path);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe([parameter]);
    }

    [Fact]
    public async Task AbsentQueryFilter_IsUnfiltered()
    {
        (await _client.GetStringAsync("/api/things")).ShouldBe("0");
    }

    [Theory]
    [InlineData("/api/systems/{systemKey}/things", 404)]
    [InlineData("/api/renamed", 400)]
    public void Endpoint_DeclaresTheProblemTheSeamAnswers(string route, int status)
    {
        var problems = Endpoint(_app, route).Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(metadata => metadata.StatusCode >= 400)
            .ToArray();
        problems.Select(problem => problem.StatusCode).ShouldBe(status == 400 ? [400] : [400, status], ignoreOrder: true);
        foreach (var problem in problems)
        {
            problem.Type.ShouldBe(typeof(ProblemDetails));
            problem.ContentTypes.ShouldBe(["application/problem+json"]);
        }
    }

    [Fact]
    public async Task AsParametersBinding_IsRejectedWhenTheEndpointIsBuilt()
    {
        await using var app = Build(api =>
            api.MapGet("/grouped", ([AsParameters] SystemKeyQuery query) => query.SystemKey?.SystemId ?? 0));
        await app.StartAsync();
        using var client = app.GetTestClient();

        // Routing builds the endpoint on the first request, invoking the metadata hook by reflection.
        var thrown = await Should.ThrowAsync<Exception>(() => client.GetAsync("/api/grouped?systemKey=snes"));
        thrown.GetBaseException().ShouldBeOfType<NotSupportedException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostEndpoints_BindEveryRouteAndQuerySystemKeyThroughTheSeamInTheStage(bool consumer)
    {
        await using var host = StartedHost.Start(consumer);
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        HandlerEndpoints(endpoints).Where(endpoint => endpoint.Parameters.Any(parameter => parameter.IsSeam)).ShouldNotBeEmpty();
        SeamViolations(endpoints).ShouldBeEmpty(
            "Every route or query systemKey binds as SystemKeyParameter inside the declared-parameter stage.");
    }

    [Fact]
    public async Task SeamGuard_FlagsStringSystemKeysWhateverTheirCase_GroupedParametersAndUnverifiableRoutes()
    {
        // Inspect deliberately invalid endpoints outside the validated group: that group now
        // rejects [AsParameters] at construction, before the inventory guard can inspect it.
        await using var app = Build(api =>
        {
            api.MapGet("/systems/{systemKey}/strings", (string systemKey) => systemKey);
            api.MapGet("/strings", ([FromQuery(Name = "SystemKey")] string? key) => key);
            api.MapGet("/grouped", ([AsParameters] StringSystemKeyQuery query) => query.SystemKey);
            api.MapGet("/systems/{systemKey}/delegate", context => Task.CompletedTask);
            api.MapGet("/plain-delegate", context => Task.CompletedTask);
        }, validateParameters: false);

        var violations = SeamViolations(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));

        violations.ShouldContain(violation => violation.Contains("/systems/{systemKey}/strings") && violation.Contains("String systemKey"));
        violations.ShouldContain(violation => violation.Contains("/strings") && violation.Contains("String key"));
        violations.ShouldContain(violation => violation.Contains("/grouped") && violation.Contains("[AsParameters]"));
        violations.ShouldContain(violation => violation.Contains("/systems/{systemKey}/delegate") && violation.Contains("no handler method"));
        violations.ShouldContain(violation => violation.Contains("/api/plain-delegate") && violation.Contains("no handler method"));
    }

    /// <summary>
    ///     Every way a route or query <c>systemKey</c> can bypass the seam. Wire names compare without case, as
    ///     binding does; an <c>/api</c> route, or any route naming <c>{systemKey}</c>, without a handler method
    ///     cannot be checked, and <c>[AsParameters]</c> would hide a member's binding, so both fail.
    /// </summary>
    private static string[] SeamViolations(IEnumerable<Endpoint> endpoints)
    {
        var routes = endpoints.OfType<RouteEndpoint>().ToArray();
        var handlers = HandlerEndpoints(routes);
        var unverifiable = routes
            .Where(route => route.Metadata.GetMetadata<MethodInfo>() is null
                && (route.RoutePattern.GetParameter(SystemKey) is not null || IsApiRoute(route)))
            .Select(route => $"{route.RoutePattern.RawText}: no handler method to check");
        var grouped = handlers
            .Where(endpoint => endpoint.Parameters.Any(parameter => parameter.Info.IsDefined(typeof(AsParametersAttribute), inherit: true)))
            .Select(endpoint => $"{endpoint.Route.RoutePattern.RawText}: [AsParameters] hides its members' bindings");
        var unbound = handlers
            .Where(endpoint => endpoint.Route.RoutePattern.GetParameter(SystemKey) is not null
                && !endpoint.Parameters.Any(parameter => parameter.IsSeam && IsSystemKey(parameter.WireName)))
            .Select(endpoint => $"{endpoint.Route.RoutePattern.RawText}: route {{{SystemKey}}}");
        var stringBound = handlers
            .SelectMany(endpoint => endpoint.Parameters
                .Where(parameter => IsSystemKey(parameter.WireName) && !parameter.IsSeam && parameter.FromRouteOrQuery)
                .Select(parameter => $"{endpoint.Route.RoutePattern.RawText}: {parameter.Info.ParameterType.Name} {parameter.Info.Name}"));
        var unvalidated = handlers
            .Where(endpoint => endpoint.Parameters.Any(parameter => parameter.IsSeam)
                && endpoint.Route.Metadata.GetMetadata<ValidatedParametersMetadata>() is null)
            .Select(endpoint => $"{endpoint.Route.RoutePattern.RawText}: outside a WithRomdParameterValidation group");
        return [.. unverifiable, .. grouped, .. unbound, .. stringBound, .. unvalidated];
    }

    private static bool IsApiRoute(RouteEndpoint route) =>
        (route.RoutePattern.RawText ?? string.Empty).TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSystemKey(string wireName) => string.Equals(wireName, SystemKey, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<HandlerEndpoint> HandlerEndpoints(IEnumerable<Endpoint> endpoints) =>
        endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<MethodInfo>() is not null)
            .Select(endpoint => new HandlerEndpoint(endpoint, endpoint.Metadata.GetMetadata<MethodInfo>()!.GetParameters()
                .Select(parameter => (Info: parameter, WireName: ParameterWireName.Of(parameter)))
                .Select(parameter => new BoundParameter(parameter.Info, parameter.WireName,
                    endpoint.RoutePattern.GetParameter(parameter.WireName) is not null))
                .ToArray()))
            .ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveOpenApi_PublishesEveryBoundSystemKeyAsAString(bool consumer)
    {
        await using var host = StartedHost.Start(consumer);
        using var document = await host.ReadOpenApiAsync();
        var paths = document.RootElement.GetProperty("paths");

        var bindings = HandlerEndpoints(host.Services.GetRequiredService<EndpointDataSource>().Endpoints)
            .Where(endpoint => endpoint.Route.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() is not { ExcludeFromDescription: true })
            .SelectMany(endpoint => endpoint.Parameters.Where(parameter => parameter.IsSeam)
                .SelectMany(parameter => endpoint.Methods.Select(method => (endpoint, parameter, method))))
            .ToArray();
        var unpublished = bindings
            .Where(binding => !IsPublishedAsString(paths, binding.endpoint, binding.parameter, binding.method))
            .Select(binding => $"{binding.method} {binding.endpoint.Route.RoutePattern.RawText} {binding.parameter.WireName}");

        bindings.ShouldNotBeEmpty();
        unpublished.ShouldBeEmpty();
    }

    private static WebApplication Build(Action<RouteGroupBuilder> map, bool validateParameters = true)
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.ResolveSystemIdAsync("snes", Arg.Any<CancellationToken>()).Returns(SnesId);
        catalog.ResolveSystemIdAsync(Arg.Is<string>(key => key != "snes"), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRomdHttpContract();
        builder.Services.AddSingleton(catalog);
        var app = builder.Build();
        app.UseRomdExceptionHandling();
        var api = app.MapGroup("/api");
        map(validateParameters ? api.WithRomdParameterValidation() : api);
        return app;
    }

    private static bool IsPublishedAsString(JsonElement paths, HandlerEndpoint endpoint, BoundParameter parameter, string method)
    {
        string path = "/" + RouteConstraint().Replace(endpoint.Route.RoutePattern.RawText ?? string.Empty, "}").Trim('/');
        string location = parameter.InRoute ? "path" : "query";
        return paths.TryGetProperty(path, out var item)
            && item.TryGetProperty(method.ToLowerInvariant(), out var operation)
            && operation.TryGetProperty("parameters", out var parameters)
            && parameters.EnumerateArray().Any(published =>
                published.GetProperty("name").GetString() == parameter.WireName
                && published.GetProperty("in").GetString() == location
                && published.GetProperty("schema").TryGetProperty("type", out var type)
                && type.GetString() == "string");
    }

    [GeneratedRegex(@"[:?][^}]*\}|\?\}")]
    private static partial Regex RouteConstraint();

    private static RouteEndpoint Endpoint(WebApplication app, string route) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);

    public readonly record struct SystemKeyQuery(SystemKeyParameter? SystemKey);

    public readonly record struct StringSystemKeyQuery(string? SystemKey);

    private const string SystemKey = "systemKey";

    private sealed record BoundParameter(ParameterInfo Info, string WireName, bool InRoute)
    {
        public bool IsSeam => Info.ParameterType == typeof(SystemKeyParameter);

        public bool FromRouteOrQuery => !Info.GetCustomAttributes(inherit: true).Any(attribute =>
            attribute is IFromBodyMetadata or IFromFormMetadata or IFromHeaderMetadata or IFromServiceMetadata);
    }

    private sealed record HandlerEndpoint(RouteEndpoint Route, IReadOnlyList<BoundParameter> Parameters)
    {
        public IEnumerable<string> Methods => Route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
    }

    /// <summary>A started host (Development, so it serves its OpenAPI document).</summary>
    private sealed class StartedHost : IAsyncDisposable
    {
        private const string Secret = "system-key-stage-secret-at-least-32-characters";

        private readonly IAsyncDisposable _factory;
        private readonly HttpClient _client;
        private readonly string _dataDirectory;
        private readonly bool _consumer;

        private StartedHost(IAsyncDisposable factory, HttpClient client, IServiceProvider services, string dataDirectory, bool consumer)
        {
            _factory = factory;
            _client = client;
            Services = services;
            _dataDirectory = dataDirectory;
            _consumer = consumer;
        }

        public IServiceProvider Services { get; }

        public static StartedHost Start(bool consumer)
        {
            string dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-system-key-stage-{Guid.NewGuid():N}");
            return consumer
                ? Start(RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(dataDirectory, "Development", Secret), dataDirectory, consumer)
                : Start(RomdHostTestFactory.Create<Romd.Admin.Host.Program>(dataDirectory, "Development", Secret), dataDirectory, consumer);
        }

        public async Task<JsonDocument> ReadOpenApiAsync() =>
            JsonDocument.Parse(await _client.GetStringAsync($"/openapi/{(_consumer ? "consumer-v1" : "admin-v1")}.json"));

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }

        private static StartedHost Start<TProgram>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TProgram> factory,
            string dataDirectory, bool consumer)
            where TProgram : class =>
            new(factory, factory.CreateClient(), factory.Services, dataDirectory, consumer);
    }
}
