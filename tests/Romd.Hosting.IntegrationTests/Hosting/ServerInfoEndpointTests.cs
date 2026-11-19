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

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("{\"releaseTag\": \"\"}")]
    public async Task GetServerInfo_DeploymentManifestUnavailable_ReportsTheBuildWithoutARelease(string? manifest)
    {
        using var body = await GetServerInfoAsync(manifest);

        body.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["release", "build"]);
        body.RootElement.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        AssertBuild(body.RootElement.GetProperty("build"));
    }

    [Theory]
    [InlineData("v0.4.0")]
    [InlineData("v0.4.0-rc.1")]
    public async Task GetServerInfo_DeploymentManifestPresent_ReportsTheInstalledReleaseBesideTheBuild(string releaseTag)
    {
        using var body = await GetServerInfoAsync(JsonSerializer.Serialize(new { releaseTag }));

        body.RootElement.GetProperty("release").GetString().ShouldBe(releaseTag);
        AssertBuild(body.RootElement.GetProperty("build"));
    }

    private async Task<JsonDocument> GetServerInfoAsync(string? manifest)
    {
        string path = Path.Combine(_dataDirectory.FullName, "manifest.json");
        if (manifest is not null)
        {
            await File.WriteAllTextAsync(path, manifest);
        }

        await using var factory = RomdHostTestFactory.CreateAdmin(_dataDirectory.FullName, TestJwtSecret,
            services => services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            new Dictionary<string, string> { ["Romd:DeploymentManifestPath"] = path });
        using var client = factory.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);

        using var response = await client.GetAsync("/api/server/info");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // The build never takes its identity from the bundle: it is what the image was compiled with.
    private static void AssertBuild(JsonElement build)
    {
        build.EnumerateObject().Select(property => property.Name).ShouldBe(["tag", "revision"]);
        build.GetProperty("tag").GetString().ShouldBe(BuildMetadata("RomdReleaseTag"));
        build.GetProperty("revision").GetString().ShouldBe(BuildMetadata("RomdSourceRevision"));
    }

    private static string BuildMetadata(string key)
    {
        string? value = typeof(Romd.Admin.Host.Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == key).Value;
        value.ShouldNotBeNullOrWhiteSpace();
        return value;
    }

    public void Dispose() => _dataDirectory.Delete(recursive: true);
}
