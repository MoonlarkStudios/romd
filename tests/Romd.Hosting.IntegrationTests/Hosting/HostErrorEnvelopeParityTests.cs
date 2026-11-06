using Romd.Contracts.Common.Models;
using Microsoft.AspNetCore.Routing;
using System.Text.RegularExpressions;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.Libraries.Queries.GetLibraryById;
using Romd.Admin.Application.Libraries.Queries.ListLibraries;
using Romd.Application.Common.Cqrs;
using Romd.Consumer.Application.Auth.Queries.GetCurrentConsumerUser;
using Romd.Consumer.Application.Browse.Queries.GetConsumerTitle;
using Romd.Contracts.Consumer.Auth;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Both hosts share the error contract (docs/decisions/api-contract-policy.md,
///     "Canonical Hosts And Contract Documents"): unmatched /api routes answer with the 404
///     envelope, a wrong method keeps its 405 and <c>Allow</c> header even where the SPA serves
///     client routes, and error envelopes carry CORS headers so browser clients can read them.
/// </summary>
public sealed class HostErrorEnvelopeParityTests : IDisposable
{
    private const string TestJwtSecret = "host-error-envelope-parity-secret-at-least-32-characters";
    private const string AdminOrigin = "https://admin.romd.test";
    private const string ConsumerOrigin = "https://consumer.romd.test";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"romd-host-error-envelope-parity-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmatchedApiRoute_Returns404Envelope(bool consumer)
    {
        await using var host = CreateHost(consumer);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/no-such-resource/42");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Request.RouteNotFound");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongMethodOnExistingRoute_Returns405Envelope(bool consumer)
    {
        await using var host = CreateHost(consumer);
        using var client = host.CreateClient();

        using var response = await client.DeleteAsync(consumer ? "/api/me" : "/api/libraries");

        using var body = await ProblemEnvelope.ReadAsync(response, 405, "Request.MethodNotAllowed");
        response.Content.Headers.Allow.ShouldNotBeEmpty();
        response.Content.Headers.Allow.ShouldNotContain("DELETE");
    }

    [Theory]
    [InlineData(false, "/api/libraries/abc123/configuration/preview")]
    [InlineData(true, "/api/disc-sets/abc123/access/preview")]
    public async Task GetOnPostOnlyRoute_Returns405EnvelopeWithAllow(bool consumer, string path)
    {
        await using var host = CreateHost(consumer);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path);

        using var body = await ProblemEnvelope.ReadAsync(response, 405, "Request.MethodNotAllowed");
        response.Content.Headers.Allow.ShouldBe(["POST"]);
    }

    [Fact]
    public async Task AdminSpaClientRoutes_MatchTheHostFallbacksAndServeTheClientApplication()
    {
        string routes = File.ReadAllText(RepositoryPaths.FindRepositoryFile("web/packages/romd-admin-app/src/routes.tsx"));
        string[] clientPrefixes = Regex.Matches(routes, @"\bpath:\s*(?<quote>['""])(?<path>.*?)\k<quote>")
            .Select(match => match.Groups["path"].Value.Trim('/').Split('/')[0])
            .Where(prefix => prefix.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var adminHost = CreateAdminHost();
        await using var host = new HostUnderTest(adminHost, adminHost.CreateClient);
        string[] fallbackPrefixes = adminHost.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Order == int.MaxValue)
            .Select(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/').Split('/')[0])
            .Where(prefix => prefix.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        using var client = host.CreateClient();

        clientPrefixes.ShouldNotBeEmpty();
        fallbackPrefixes.ShouldBe(clientPrefixes,
            "src/Romd.Admin.Host/Program.cs must serve exactly the top-level routes of routes.tsx.");
        foreach (string path in clientPrefixes.Select(prefix => $"/{prefix}/deep/link").Prepend("/"))
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html", path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedRequestMediaType_Returns415Envelope(bool consumer)
    {
        await using var host = CreateHost(consumer);
        using var client = host.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");

        using var response = consumer
            ? await client.PatchAsync("/api/me/settings", content)
            : await client.PostAsync("/api/libraries", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 415, "Request.UnsupportedMediaType");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryNameRefusal_FollowsAuthenticationAndCors(bool consumer)
    {
        await using var host = CreateHost(consumer);
        string origin = consumer ? ConsumerOrigin : AdminOrigin;
        string path = consumer ? "/api/me?x=1" : "/api/libraries?x=1";
        using var anonymous = host.CreateClient();
        using var client = host.CreateClient()
            .WithTestUser(Guid.NewGuid(), roles: [consumer ? RomdRoleType.User : RomdRoleType.Admin]);

        // Authentication decides first: an anonymous caller is challenged, whatever its query.
        (await anonymous.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var refused = new HttpRequestMessage(HttpMethod.Get, path);
        refused.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(refused);
        using (var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter"))
            body.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["x"]);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([origin]);

        // A preflight never reaches the operation, so its query is not the operation's.
        using var preflight = new HttpRequestMessage(HttpMethod.Options, path);
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var preflightResponse = await anonymous.SendAsync(preflight);
        preflightResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([origin]);
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(false, 404)]
    [InlineData(false, 405)]
    [InlineData(false, 500)]
    [InlineData(true, 400)]
    [InlineData(true, 404)]
    [InlineData(true, 405)]
    [InlineData(true, 500)]
    public async Task ErrorEnvelope_FromAllowedOrigin_CarriesCorsHeaders(bool consumer, int status)
    {
        await using var host = CreateHost(consumer);
        using var client = host.CreateClient()
            .WithTestUser(Guid.NewGuid(), roles: [consumer ? RomdRoleType.User : RomdRoleType.Admin]);
        string origin = consumer ? ConsumerOrigin : AdminOrigin;
        using var request = new HttpRequestMessage(status == 405 ? HttpMethod.Delete : HttpMethod.Get, (consumer, status) switch
        {
            (_, 404) => "/api/no-such-resource/42",
            (true, 400) => "/api/titles/not-a-sqid!",
            (true, _) => "/api/me",
            (false, 400) => "/api/libraries/not-a-sqid!",
            (false, _) => "/api/libraries"
        });
        request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request);

        using var body = await ProblemEnvelope.ReadAsync(response, status, status switch
        {
            400 => "Request.InvalidParameter",
            404 => "Request.RouteNotFound",
            405 => "Request.MethodNotAllowed",
            _ => "General.Unhandled"
        });
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([origin]);
    }

    private WebApplicationFactory<Romd.Admin.Host.Program> CreateAdminHost() =>
        RomdHostTestFactory.CreateAdmin(
            _dataDirectory,
            TestJwtSecret,
            services =>
            {
                services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                services.ReplaceWith(Substitute.For<IQueryHandler<GetLibraryByIdQuery, LibraryDto>>());
                var list = Substitute.For<IQueryHandler<ListLibrariesQuery, Page<LibraryDto>>>();
                list.HandleAsync(Arg.Any<ListLibrariesQuery>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(new InvalidOperationException("admin-cors-unhandled"));
                services.ReplaceWith(list);
            },
            new Dictionary<string, string> { ["Romd:AdminHost:CorsOrigins:0"] = AdminOrigin });

    private WebApplicationFactory<Romd.Consumer.Host.Program> CreateConsumerHost() =>
        RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(
            _dataDirectory,
            "Testing",
            TestJwtSecret,
            services =>
            {
                services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                services.ReplaceWith(Substitute.For<IQueryHandler<GetConsumerTitleQuery, ConsumerTitleDetailDto>>());
                var me = Substitute.For<IQueryHandler<GetCurrentConsumerUserQuery, CurrentUserDto>>();
                me.HandleAsync(Arg.Any<GetCurrentConsumerUserQuery>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(new InvalidOperationException("consumer-cors-unhandled"));
                services.ReplaceWith(me);
            },
            new Dictionary<string, string> { ["Romd:ConsumerHost:CorsOrigins:0"] = ConsumerOrigin });

    private HostUnderTest CreateHost(bool consumer)
    {
        if (consumer)
        {
            var consumerHost = CreateConsumerHost();
            return new HostUnderTest(consumerHost, consumerHost.CreateClient);
        }

        var adminHost = CreateAdminHost();
        return new HostUnderTest(adminHost, adminHost.CreateClient);
    }

    /// <summary>Lets one test body drive either host's factory.</summary>
    private sealed record HostUnderTest(IAsyncDisposable Factory, Func<HttpClient> CreateClient) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }
}
