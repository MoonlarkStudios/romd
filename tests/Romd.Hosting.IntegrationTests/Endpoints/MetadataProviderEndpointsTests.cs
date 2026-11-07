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
    private const string SettingsPath = "/api/metadata-providers/igdb/settings";

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
            using var saved = await PutAsync(client,
                new UpdateIgdbProviderSettingsRequest(false, "integration-client", secret));
            saved.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await saved.Content.ReadAsStringAsync()).ShouldNotContain(secret);

            using var read = await client.GetAsync(SettingsPath);
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await read.Content.ReadAsStringAsync()).ShouldNotContain(secret);
            var settings = await read.Content.ReadFromJsonAsync<IgdbProviderSettings>();
            settings.ShouldNotBeNull();
            settings.ClientId.ShouldBe("integration-client");
            settings.HasClientSecret.ShouldBeTrue();
            settings.Enabled.ShouldBeFalse();
            settings.ManagedByDeployment.ShouldBeFalse();

            using var retained = await PutAsync(client,
                new UpdateIgdbProviderSettingsRequest(false, "integration-client", null));
            retained.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await retained.Content.ReadFromJsonAsync<IgdbProviderSettings>())!.HasClientSecret.ShouldBeTrue();
        }
        finally
        {
            using var cleared = await PutAsync(client,
                new UpdateIgdbProviderSettingsRequest(false, null, null, ClearClientSecret: true));
            cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await cleared.Content.ReadFromJsonAsync<IgdbProviderSettings>())!.HasClientSecret.ShouldBeFalse();

            using var tested = await TestAsync(client);
            tested.StatusCode.ShouldBe(HttpStatusCode.OK);
            var testResult = await tested.Content.ReadFromJsonAsync<IgdbProviderSettings>();
            testResult.ShouldNotBeNull();
            testResult.LastTestedAt.ShouldNotBeNull();
            testResult.LastTestSucceeded.ShouldBe(false);
            testResult.IsConfigured.ShouldBeFalse();
        }
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, UpdateIgdbProviderSettingsRequest body)
    {
        using var read = await client.GetAsync(SettingsPath);
        using var request = new HttpRequestMessage(HttpMethod.Put, SettingsPath) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag).ShouldBeTrue();
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> TestAsync(HttpClient client)
    {
        using var read = await client.GetAsync(SettingsPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, SettingsPath + "/test-connection");
        request.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag).ShouldBeTrue();
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage CreateRequest(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method == "PUT"
            ? JsonContent.Create(new UpdateIgdbProviderSettingsRequest(false, null, null))
            : null
    };
}
