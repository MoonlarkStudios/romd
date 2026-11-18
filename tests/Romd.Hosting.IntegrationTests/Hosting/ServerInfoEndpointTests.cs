using System.Net;
using System.Reflection;
using System.Text.Json;
using OpenIddict.Validation.AspNetCore;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ServerInfoEndpointTests : IDisposable
{
    private const string TestJwtSecret = "server-info-test-signing-secret-at-least-32-chars";
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("romd-server-info-");

    [Fact]
    public async Task GetServerInfo_AnonymousRequest_ReceivesBearerChallenge()
    {
        await using var factory = RomdHostTestFactory.CreateAdmin(_dataDirectory.FullName, TestJwtSecret);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/server/info");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    [Fact]
    public async Task GetServerInfo_AuthenticatedRequest_ReturnsAdminBuildVersion()
    {
        await using var factory = RomdHostTestFactory.CreateAdmin(_dataDirectory.FullName, TestJwtSecret,
            services => services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme));
        using var client = factory.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        string? releaseTag = typeof(Romd.Admin.Host.Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RomdReleaseTag").Value;

        using var response = await client.GetAsync("/api/server/info");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["version"]);
        releaseTag.ShouldNotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("version").GetString().ShouldBe(releaseTag);
    }

    public void Dispose() => _dataDirectory.Delete(recursive: true);
}
