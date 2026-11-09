using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Common.Models;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [Fact]
    public async Task VfsConnections_OwnerLifecycle_UsesScopedHttpsBasicCredentials()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync(configureHost: builder =>
        {
            builder.UseSetting("Romd:Vfs:Enabled", "true");
            builder.UseSetting("Romd:ConsumerHost:PublicUrl", "https://library.example.test");
        });
        int libraryId = await fixture.CreateLibraryAsync("VFS Library");
        var user = await fixture.CreateConsumerUserAsync("vfs-owner", "vfs-owner@localhost", "Password123",
            user => user.LibraryId = libraryId);
        using var owner = fixture.Factory.CreateClient().WithTestUser(user.Id, roles: [RomdRoleType.User]);
        using var created = await owner.PostAsJsonAsync("/api/me/connections", new SaveVfsConnectionRequest("Steam Deck"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var first = (await created.Content.ReadFromJsonAsync<VfsConnectionCredential>())!;
        first.Endpoint.ShouldBe("https://library.example.test/vfs/library/");
        first.RcloneConfig.ShouldContain("type = webdav");
        first.RcloneConfig.ShouldContain("vendor = owncloud");
        first.RcloneConfig.ShouldNotContain(first.Password);
        string path = $"/api/me/connections/{first.Connection.Id}";
        using var mount = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        void UsePassword(string password) => mount.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{first.Connection.Username}:{password}")));
        UsePassword(first.Password);
        // OPTIONS exercises actual authentication and the read-only endpoint without a published projection.
        using var allowed = await mount.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/vfs/library/"));
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var apiDenied = await mount.GetAsync("/api/me/connections");
        apiDenied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var httpDenied = await mount.SendAsync(new HttpRequestMessage(HttpMethod.Options, "http://localhost/vfs/library/"));
        httpDenied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        string listing = await owner.GetStringAsync("/api/me/connections");
        listing.ShouldNotContain(first.Password);
        listing.ShouldNotContain("password", Case.Insensitive);
        using var renamed = await owner.PatchAsJsonAsync(path, new SaveVfsConnectionRequest("Living room"));
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var rotated = await owner.PostAsync(path + "/rotate", null);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await rotated.Content.ReadFromJsonAsync<VfsConnectionCredential>())!;
        using var stale = await mount.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/vfs/library/"));
        stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        UsePassword(second.Password);
        using var fresh = await mount.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/vfs/library/"));
        fresh.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var passwordChange = await owner.PutAsJsonAsync("/api/me/password", new ChangePasswordRequest("Password123", "Password456"));
        passwordChange.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var invalidated = await mount.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/vfs/library/"));
        invalidated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var revoked = await owner.DeleteAsync(path);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var resurrection = await owner.PostAsync(path + "/rotate", null);
        resurrection.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task VfsConnections_FormatDefaultsAndUpdates_ArePerConnectionAndKeepIssuedCredential()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync(configureHost: builder =>
        {
            builder.UseSetting("Romd:Vfs:Enabled", "true");
            builder.UseSetting("Romd:ConsumerHost:PublicUrl", "https://library.example.test");
        });
        var user = await fixture.CreateConsumerUserAsync("format-owner", "format-owner@localhost", "Password123");
        using var owner = fixture.Factory.CreateClient().WithTestUser(user.Id, roles: [RomdRoleType.User]);
        using var created = await owner.PostAsJsonAsync("/api/me/connections", new SaveVfsConnectionRequest("Stored"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = (await created.Content.ReadFromJsonAsync<VfsConnectionCredential>())!;
        first.Connection.Format.ShouldBe("stored");
        using var secondResponse = await owner.PostAsJsonAsync("/api/me/connections", new SaveVfsConnectionRequest("Original", "original"));
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var second = (await secondResponse.Content.ReadFromJsonAsync<VfsConnectionCredential>())!;
        second.Connection.Format.ShouldBe("original");
        string path = $"/api/me/connections/{first.Connection.Id}";
        using var changed = await owner.PatchAsJsonAsync(path, new SaveVfsConnectionRequest("Stored updated", "original"));
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<VfsConnection>())!.Format.ShouldBe("original");
        using var renamed = await owner.PatchAsJsonAsync(path, new SaveVfsConnectionRequest("Renamed"));
        (await renamed.Content.ReadFromJsonAsync<VfsConnection>())!.Format.ShouldBe("original");
        var listed = (await owner.GetFromJsonAsync<Page<VfsConnection>>("/api/me/connections"))!.Items;
        listed.ShouldAllBe(connection => connection.Format == "original");
        using var rotated = await owner.PostAsync(path + "/rotate", null);
        (await rotated.Content.ReadFromJsonAsync<VfsConnectionCredential>())!.Connection.Format.ShouldBe("original");
    }

    [Theory]
    [InlineData("\"Stored\"")]
    [InlineData("\" original\"")]
    [InlineData("\"original \"")]
    [InlineData("\"rvz\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    public async Task VfsConnections_InvalidFormat_DoesNotIssueOrUpdate(string jsonFormat)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync(configureHost: builder =>
        {
            builder.UseSetting("Romd:Vfs:Enabled", "true");
            builder.UseSetting("Romd:ConsumerHost:PublicUrl", "https://library.example.test");
        });
        var user = await fixture.CreateConsumerUserAsync("invalid-format", "invalid-format@localhost", "Password123");
        using var owner = fixture.Factory.CreateClient().WithTestUser(user.Id, roles: [RomdRoleType.User]);
        using var invalidCreate = await owner.PostAsync("/api/me/connections", new StringContent(
            "{\"name\":\"Invalid\",\"format\":" + jsonFormat + "}", Encoding.UTF8, "application/json"));
        invalidCreate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.GetFromJsonAsync<Page<VfsConnection>>("/api/me/connections"))!.Items.ShouldBeEmpty();
        using var created = await owner.PostAsJsonAsync("/api/me/connections", new SaveVfsConnectionRequest("Device", "original"));
        var first = (await created.Content.ReadFromJsonAsync<VfsConnectionCredential>())!;
        using var invalidUpdate = await owner.PatchAsync($"/api/me/connections/{first.Connection.Id}", new StringContent(
            "{\"name\":\"Invalid\",\"format\":" + jsonFormat + "}", Encoding.UTF8, "application/json"));
        invalidUpdate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var current = (await owner.GetFromJsonAsync<Page<VfsConnection>>("/api/me/connections"))!.Items.ShouldHaveSingleItem();
        current.Name.ShouldBe("Device");
        current.Format.ShouldBe("original");
        using var anonymous = fixture.Factory.CreateClient();
        using var denied = await anonymous.PatchAsJsonAsync($"/api/me/connections/{first.Connection.Id}", new SaveVfsConnectionRequest("Stolen", "stored"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await owner.GetFromJsonAsync<Page<VfsConnection>>("/api/me/connections"))!.Items.Single().Format.ShouldBe("original");
    }

    [Fact]
    public async Task VfsConnections_MissingPublicHttpsOrigin_DoesNotIssueCredential()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var user = await fixture.CreateConsumerUserAsync("vfs-owner", "vfs-owner@localhost", "Password123");
        using var owner = fixture.Factory.CreateClient().WithTestUser(user.Id, roles: [RomdRoleType.User]);
        using var response = await owner.PostAsJsonAsync("/api/me/connections", new SaveVfsConnectionRequest("Deck"));
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await owner.GetFromJsonAsync<Page<VfsConnection>>("/api/me/connections"))!.Items.ShouldBeEmpty();
    }
}
