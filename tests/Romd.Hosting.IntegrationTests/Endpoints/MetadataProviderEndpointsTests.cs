using System.Net;
using System.Net.Http.Json;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class MetadataProviderEndpointsTests(IntegrationTestFixture fixture)
{
    private const string SettingsPath = "/api/admin/metadata-providers/igdb";

    [Theory]
    [InlineData("GET", SettingsPath)]
    [InlineData("PUT", SettingsPath)]
    [InlineData("POST", SettingsPath + "/test-connection")]
    public async Task MetadataProviderManagement_Anonymous_ReturnsUnauthorized(string method, string path)
    {
        using var client = fixture.CreateClient();
        using var request = CreateRequest(method, path);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", SettingsPath, RomdRoleType.User)]
    [InlineData("PUT", SettingsPath, RomdRoleType.User)]
    [InlineData("POST", SettingsPath + "/test-connection", RomdRoleType.User)]
    [InlineData("GET", SettingsPath, RomdRoleType.Manager)]
    [InlineData("PUT", SettingsPath, RomdRoleType.Manager)]
    [InlineData("POST", SettingsPath + "/test-connection", RomdRoleType.Manager)]
    public async Task MetadataProviderManagement_NonAdmin_ReturnsForbidden(
        string method, string path, RomdRoleType role)
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [role]);
        using var request = CreateRequest(method, path);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MetadataProviderManagement_Admin_CanSaveReadAndClearWriteOnlySecret()
    {
        using var client = fixture.CreateAuthenticatedClient();
        const string secret = "integration-igdb-write-only-secret";

        try
        {
            using var saved = await client.PutAsJsonAsync(SettingsPath,
                new UpdateIgdbProviderSettingsRequest(false, "integration-client", secret) { Revision = (await client.GetFromJsonAsync<IgdbProviderSettingsDto>(SettingsPath))!.Revision });
            saved.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await saved.Content.ReadAsStringAsync()).ShouldNotContain(secret);

            using var read = await client.GetAsync(SettingsPath);
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await read.Content.ReadAsStringAsync()).ShouldNotContain(secret);
            var settings = await read.Content.ReadFromJsonAsync<IgdbProviderSettingsDto>();
            settings.ShouldNotBeNull();
            settings.ClientId.ShouldBe("integration-client");
            settings.HasClientSecret.ShouldBeTrue();
            settings.Enabled.ShouldBeFalse();
            settings.ManagedByDeployment.ShouldBeFalse();

            using var retained = await client.PutAsJsonAsync(SettingsPath,
                new UpdateIgdbProviderSettingsRequest(false, "integration-client", null) { Revision = settings.Revision });
            retained.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await retained.Content.ReadFromJsonAsync<IgdbProviderSettingsDto>())!.HasClientSecret.ShouldBeTrue();
        }
        finally
        {
            using var cleared = await client.PutAsJsonAsync(SettingsPath,
                new UpdateIgdbProviderSettingsRequest(false, null, null, ClearClientSecret: true) { Revision = (await client.GetFromJsonAsync<IgdbProviderSettingsDto>(SettingsPath))!.Revision });
            cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await cleared.Content.ReadFromJsonAsync<IgdbProviderSettingsDto>())!.HasClientSecret.ShouldBeFalse();

            using var tested = await client.PostAsync(SettingsPath + "/test-connection", null);
            tested.StatusCode.ShouldBe(HttpStatusCode.OK);
            var testResult = await tested.Content.ReadFromJsonAsync<IgdbProviderSettingsDto>();
            testResult.ShouldNotBeNull();
            testResult.LastTestedAt.ShouldNotBeNull();
            testResult.LastTestSucceeded.ShouldBe(false);
            testResult.IsConfigured.ShouldBeFalse();
        }
    }

    private static HttpRequestMessage CreateRequest(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method == "PUT"
            ? JsonContent.Create(new UpdateIgdbProviderSettingsRequest(false, null, null))
            : null
    };
}
