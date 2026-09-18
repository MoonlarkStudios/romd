using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ArtworkCurationEndpointsTests(IntegrationTestFixture fixture)
{
    private const string Settings = "/api/admin/artwork-providers/steamgriddb";

    [Fact]
    public async Task Providers_RegistryIncludesBothAdaptersAndTitleScopeValidatesInput()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/artwork/providers");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var providers = (await response.Content.ReadFromJsonAsync<ArtworkProviderDto[]>())!;
        providers!.Select(provider => provider.Id).ShouldContain("igdb");
        providers.Select(provider => provider.Id).ShouldContain("steamgriddb");
        using var invalid = await client.GetAsync($"/api/artwork/providers?titleId={IdCoder.Encode(1)}&role=invalid");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var missing = await client.GetAsync($"/api/artwork/providers?titleId={IdCoder.Encode(int.MaxValue)}&role=Hero");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("GET", Settings)]
    [InlineData("PUT", Settings)]
    [InlineData("POST", Settings + "/test-connection")]
    [InlineData("GET", "/api/artwork/providers")]
    [InlineData("GET", "/api/artwork/titles/test/saved")]
    [InlineData("POST", "/api/artwork/titles/test/gallery")]
    public async Task ArtworkEndpoints_Anonymous_RequireAuthentication(string method, string path)
    {
        using var client = fixture.CreateClient();
        using var request = CreateRequest(method, path);
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", Settings, RomdRoleType.User)]
    [InlineData("PUT", Settings, RomdRoleType.User)]
    [InlineData("POST", Settings + "/test-connection", RomdRoleType.User)]
    [InlineData("GET", Settings, RomdRoleType.Manager)]
    [InlineData("PUT", Settings, RomdRoleType.Manager)]
    [InlineData("POST", Settings + "/test-connection", RomdRoleType.Manager)]
    public async Task Settings_NonAdmin_CannotReadOrChangeCredentials(string method, string path, RomdRoleType role)
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [role]);
        using var request = CreateRequest(method, path);
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    [InlineData("gallery")]
    public async Task CandidateEndpoints_ArbitraryUrl_IsRejectedWithoutPersistentChanges(string operation)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var before = await Counts(db);
        var path = $"/api/artwork/titles/{IdCoder.Encode(1)}/{operation}";
        using var response = operation == "preview"
            ? await client.PostAsJsonAsync(path, new PreviewArtworkRequest("http://127.0.0.1/private"))
            : await client.PostAsJsonAsync(path, new ApplyArtworkRequest(Guid.NewGuid(), "http://127.0.0.1/private"));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Counts(db)).ShouldBe(before);
    }

    [Fact]
    public async Task Settings_Admin_StoresWriteOnlySecretAndChecksRevision()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var initial = (await client.GetFromJsonAsync<SteamGridDbSettingsDto>(Settings))!;
        const string key = "controlled-steamgriddb-secret";
        try
        {
            using var saved = await client.PutAsJsonAsync(Settings,
                new UpdateSteamGridDbSettingsRequest(initial.Revision, false, key));
            saved.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await saved.Content.ReadAsStringAsync()).ShouldNotContain(key);
            var state = (await saved.Content.ReadFromJsonAsync<SteamGridDbSettingsDto>())!;
            state.HasApiKey.ShouldBeTrue();
            state.Revision.ShouldNotBe(initial.Revision);
            using var stale = await client.PutAsJsonAsync(Settings,
                new UpdateSteamGridDbSettingsRequest(initial.Revision, false, null, true));
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            var current = (await client.GetFromJsonAsync<SteamGridDbSettingsDto>(Settings))!;
            using var cleared = await client.PutAsJsonAsync(Settings,
                new UpdateSteamGridDbSettingsRequest(current.Revision, false, null, true));
            cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static HttpRequestMessage CreateRequest(string method, string path) => new(new HttpMethod(method), path)
    {
        // Match the JSON endpoint contract so routing selects the protected settings endpoint.
        Content = method == "PUT"
            ? JsonContent.Create(new UpdateSteamGridDbSettingsRequest(Guid.NewGuid(), false, null))
            : path.EndsWith("/gallery", StringComparison.Ordinal)
                ? JsonContent.Create(new ImportGalleryArtworkRequest("reference"))
                : null
    };

    private static async Task<(int Titles, int Assets, int Selections, int Files, int Jobs)> Counts(RomdDbContext db) =>
        (await db.Titles.CountAsync(), await db.ArtworkAssets.CountAsync(), await db.ArtworkSelections.CountAsync(),
            await db.Files.CountAsync(), await db.Jobs.CountAsync());
}
