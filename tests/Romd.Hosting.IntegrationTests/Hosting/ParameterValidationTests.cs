using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using OpenIddict.Validation.AspNetCore;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     The one limit seam (docs/decisions/api-contract-policy.md, "Strict Validation" and
///     "Pagination"): a range declared on a parameter answers an out-of-range value with 400
///     <c>Request.InvalidParameter</c> naming the parameter, never a clamp; the endpoint documents
///     the problem; and every live <c>limit</c> parameter publishes its bounds.
/// </summary>
public sealed class ParameterValidationTests : IAsyncLifetime, IDisposable
{
    private const string TestJwtSecret = "parameter-validation-secret-at-least-32-characters";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"romd-parameter-validation-{Guid.NewGuid():N}");

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRomdHttpContract();
        _app = builder.Build();
        _app.UseRomdExceptionHandling();
        var api = _app.MapGroup("/api").WithRomdParameterValidation();
        api.MapGet("/items", ([Range(1, 100)] int limit = 50) => limit);
        api.MapGet("/renamed", ([FromQuery(Name = "page-size"), Range(1, 10)] int? size) => size ?? 0);
        api.MapGet("/plain", (int limit) => limit);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Theory]
    [InlineData("/api/items?limit=0", "limit")]
    [InlineData("/api/items?limit=101", "limit")]
    [InlineData("/api/renamed?page-size=11", "page-size")]
    public async Task OutOfRangeValue_Returns400NamingTheParameter(string path, string parameter)
    {
        using var response = await _client.GetAsync(path);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .ShouldBe([parameter]);
    }

    [Theory]
    [InlineData("/api/items", "50")]
    [InlineData("/api/items?limit=100", "100")]
    [InlineData("/api/renamed", "0")]
    public async Task AbsentOrInRangeValue_ReachesTheHandler(string path, string expected)
    {
        (await _client.GetStringAsync(path)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/api/items")]
    [InlineData("/api/renamed")]
    [InlineData("/api/plain")]
    public void ValidatedEndpoint_DocumentsTheProblemEvenWithoutConstraints(string route)
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);

        var problems = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(metadata => metadata.StatusCode == 400)
            .ToArray();
        var problem = problems.ShouldHaveSingleItem();
        problem.Type.ShouldBe(typeof(ProblemDetails));
        problem.ContentTypes.ShouldBe(["application/problem+json"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveOpenApi_EveryLimitPublishesItsBoundsAndProblem(bool consumer)
    {
        using var document = consumer
            ? await OpenApiTestDocuments.ReadLiveOpenApiAsync<Romd.Consumer.Host.Program>("consumer-v1", CancellationToken.None)
            : await OpenApiTestDocuments.ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", CancellationToken.None);

        // A limit still on the R10 migration allowlist is not yet on the seam.
        string host = consumer ? "consumer" : "admin";
        var limits = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation =>
                (Route: $"{host} {operation.Name.ToUpperInvariant()} {path.Name}", Operation: operation.Value)))
            .Where(entry => !ApiContractPolicyAllowlists.R10.ContainsKey($"{entry.Route} ?limit"))
            .SelectMany(entry => entry.Operation.TryGetProperty("parameters", out var parameters)
                ? parameters.EnumerateArray()
                    .Where(parameter => parameter.GetProperty("name").GetString() == "limit")
                    .Select(parameter => (entry.Route, entry.Operation, Schema: parameter.GetProperty("schema")))
                : [])
            .ToArray();

        limits.ShouldNotBeEmpty();
        limits.Where(limit => !(limit.Schema.TryGetProperty("minimum", out var minimum) && minimum.GetInt32() == 1
                && limit.Schema.TryGetProperty("maximum", out _)
                && limit.Operation.GetProperty("responses").TryGetProperty("400", out var problem)
                && problem.GetProperty("content").TryGetProperty("application/problem+json", out _)))
            .Select(limit => limit.Route)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, "/api/jobs?limit=101")]
    [InlineData(false, "/api/jobs/00000000-0000-0000-0000-000000000001/items?limit=101")]
    [InlineData(false, "/api/roms?limit=101")]
    [InlineData(false, "/api/catalog?limit=101")]
    [InlineData(true, "/api/catalog?limit=101")]
    [InlineData(true, "/api/me/library/systems?limit=101")]
    [InlineData(true, "/api/me/activity/recently-played?limit=51")]
    public async Task HostLimit_AboveItsMaximum_Returns400InsteadOfClamping(bool consumer, string path)
    {
        var (factory, client) = consumer
            ? Start(RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(_dataDirectory, "Testing", TestJwtSecret,
                services => services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)))
            : Start(RomdHostTestFactory.CreateAdmin(_dataDirectory, TestJwtSecret,
                services => services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)));
        await using var ownedFactory = factory;
        using var ownedClient = client.WithTestUser(Guid.NewGuid(), roles: [consumer ? RomdRoleType.User : RomdRoleType.Admin]);

        using var response = await client.GetAsync(path);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").TryGetProperty("limit", out _).ShouldBeTrue();
    }

    private static (IAsyncDisposable Factory, HttpClient Client) Start<TProgram>(WebApplicationFactory<TProgram> factory)
        where TProgram : class =>
        (factory, factory.CreateClient());
}
