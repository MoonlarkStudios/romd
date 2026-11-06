using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Endpoints.ConsumerDeliveryEndpointsTests;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class S6ConsumerResourceContractTests
{
    [Fact]
    public async Task SelfSettings_PatchPreservesOmittedFieldsAndPasswordUsesPut()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var userId = Guid.NewGuid();
        using var client = fixture.CreateAuthenticatedClient(userId, "s6-settings@localhost");
        using var scope = fixture.Factory.Services.CreateScope();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<RomdUser>>();
        await using (var setup = fixture.CreateSetupDbContext())
        {
            var user = await setup.Users.AsTracking().SingleAsync(candidate => candidate.Id == userId);
            user.PasswordHash = passwordHasher.HashPassword(user, "Password123");
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            await setup.SaveChangesAsync();
        }

        using var initial = await client.GetAsync("/api/me/settings");
        initial.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var first = await client.PatchAsJsonAsync("/api/me/settings", new { theme = "dark" });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = await client.PatchAsJsonAsync("/api/me/settings", new
        {
            releasePreference = new { preferredRegionKeys = Array.Empty<string>(), preferredLanguageKeys = Array.Empty<string>(), revisionStrategy = "newestFirst" }
        });
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var settings = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        settings.RootElement.GetProperty("theme").GetString().ShouldBe("dark");
        settings.RootElement.GetProperty("releasePreference").GetProperty("revisionStrategy").GetString().ShouldBe("newestFirst");

        using var changed = await client.PutAsJsonAsync("/api/me/password", new { currentPassword = "Password123", newPassword = "Password456" });
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var verification = fixture.CreateSetupDbContext();
        var updatedUser = await verification.Users.SingleAsync(candidate => candidate.Id == userId);
        updatedUser.PasswordHash.ShouldNotBeNullOrWhiteSpace();
        passwordHasher.VerifyHashedPassword(updatedUser, updatedUser.PasswordHash!, "Password123")
            .ShouldBe(PasswordVerificationResult.Failed);
        passwordHasher.VerifyHashedPassword(updatedUser, updatedUser.PasswordHash!, "Password456")
            .ShouldNotBe(PasswordVerificationResult.Failed);
    }

    [Theory]
    [InlineData("GET", "/api/account/settings")]
    [InlineData("PUT", "/api/account/settings")]
    [InlineData("POST", "/api/account/password")]
    [InlineData("GET", "/api/account/connections")]
    public async Task RetiredAccountRoutes_AuthenticatedRequest_ReturnsNotFound(string method, string path)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "s6-retired@localhost");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Fact]
    public async Task ReleaseAccess_GetReturnsPrivateNoStoreExactAllowAndRevokeFromLiveLibrary()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var userId = Guid.NewGuid();
        using var client = fixture.CreateAuthenticatedClient(userId, "s6-access@localhost", seed.LibraryId);
        string path = $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/access";

        using var allowedResponse = await client.GetAsync(path);
        allowedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        allowedResponse.Headers.CacheControl!.Private.ShouldBeTrue();
        allowedResponse.Headers.CacheControl.NoStore.ShouldBeTrue();
        using var allowed = JsonDocument.Parse(await allowedResponse.Content.ReadAsStringAsync());
        allowed.RootElement.EnumerateObject().Select(x => x.Name).Order().ShouldBe(["allowed", "releaseId", "serverInstanceId", "titleId"]);
        allowed.RootElement.GetProperty("allowed").GetBoolean().ShouldBeTrue();
        allowed.RootElement.GetProperty("releaseId").GetString().ShouldBe(IdCoder.Encode(seed.OwnedReleaseId));
        allowed.RootElement.GetProperty("titleId").GetString().ShouldBe(IdCoder.Encode(seed.OwnedTitleId));

        await fixture.ReassignUserAsync(userId, seed.OtherLibraryId);
        using var deniedResponse = await client.GetAsync(path);
        deniedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        deniedResponse.Headers.CacheControl!.Private.ShouldBeTrue();
        deniedResponse.Headers.CacheControl.NoStore.ShouldBeTrue();
        using var denied = JsonDocument.Parse(await deniedResponse.Content.ReadAsStringAsync());
        denied.RootElement.EnumerateObject().Select(x => x.Name).Order().ShouldBe(["allowed", "releaseId", "serverInstanceId"]);
        denied.RootElement.GetProperty("allowed").GetBoolean().ShouldBeFalse();
        denied.RootElement.GetProperty("releaseId").GetString().ShouldBe(IdCoder.Encode(seed.OwnedReleaseId));
        denied.RootElement.GetProperty("serverInstanceId").GetString().ShouldBe(allowed.RootElement.GetProperty("serverInstanceId").GetString());

        using var retired = await client.PostAsync(path, null);
        retired.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        using var problem = await ProblemEnvelope.ReadAsync(retired);
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(405);
    }

    [Fact]
    public async Task ReleaseAccess_UnavailableLibrary_IsConflictRatherThanAuthoritativeRevoke()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "s6-unavailable@localhost");

        using var response = await client.GetAsync($"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/access");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.TryGetProperty("allowed", out _).ShouldBeFalse();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("Chrono Trigger");
    }

    [Fact]
    public async Task Connections_CreatePublishesLocationAndPatchPreservesUnspecifiedFormat()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var userId = Guid.NewGuid();
        using var seededClient = fixture.CreateAuthenticatedClient(userId, "s6-connections@localhost");
        await using (var setup = fixture.CreateSetupDbContext())
        {
            await setup.Users.Where(user => user.Id == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.SecurityStamp, Guid.NewGuid().ToString()));
        }
        using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Romd:Vfs:Enabled", "true");
            builder.UseSetting("Romd:ConsumerHost:PublicUrl", "https://library.example.test");
        });
        using var client = factory.CreateClient().WithTestUser(userId, roles: [RomdRoleType.User]);

        using var created = await client.PostAsJsonAsync("/api/me/connections", new { name = "S6 original", format = "original" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var credential = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        string id = credential.RootElement.GetProperty("connection").GetProperty("id").GetString()!;
        string path = $"/api/me/connections/{id}";
        created.Headers.Location!.OriginalString.ShouldBe(path);
        created.Headers.CacheControl!.NoStore.ShouldBeTrue();

        using var patched = await client.PatchAsJsonAsync(path, new { name = "S6 renamed" });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var renamed = JsonDocument.Parse(await patched.Content.ReadAsStringAsync());
        renamed.RootElement.GetProperty("name").GetString().ShouldBe("S6 renamed");
        renamed.RootElement.GetProperty("format").GetString().ShouldBe("original");
        using var listResponse = await client.GetAsync("/api/me/connections?limit=1");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        AssertTerminalPage(list.RootElement, 1);
        list.RootElement.GetProperty("items")[0].GetProperty("id").GetString().ShouldBe(id);
        (await listResponse.Content.ReadAsStringAsync()).ShouldNotContain(credential.RootElement.GetProperty("password").GetString()!);
        using var rotated = await client.PostAsync(path + "/rotate", null);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var deleted = await client.DeleteAsync(path);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Collections_PageContinuationIsExplicitAndRechecksLiveLibraryScope()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        await AddCollectionAsync(fixture, seed.LibraryId, seed.PlatformId);
        var userId = Guid.NewGuid();
        using var client = fixture.CreateAuthenticatedClient(userId, "s6-collections@localhost", seed.LibraryId);

        using var firstResponse = await client.GetAsync("/api/collections?limit=1");
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        first.RootElement.GetProperty("items").GetArrayLength().ShouldBe(1);
        first.RootElement.GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        using var lastResponse = await client.GetAsync("/api/collections?limit=1&cursor=" + Uri.EscapeDataString(cursor));
        lastResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var last = JsonDocument.Parse(await lastResponse.Content.ReadAsStringAsync());
        AssertTerminalPage(last.RootElement, 1);
        last.RootElement.GetProperty("items")[0].GetProperty("id").GetString().ShouldNotBe(first.RootElement.GetProperty("items")[0].GetProperty("id").GetString());

        await fixture.ReassignUserAsync(userId, seed.OtherLibraryId);
        using var mismatch = await client.GetAsync("/api/collections?limit=1&cursor=" + Uri.EscapeDataString(cursor));
        mismatch.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RecentlyPlayed_ReturnsTerminalPageAtExactLimit()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "s6-recent@localhost", seed.LibraryId);
        using var written = await client.PutAsJsonAsync($"/api/me/activity/play-sessions/{Guid.NewGuid():D}", new
        {
            clientId = "org.romd.s6-tests",
            titleId = IdCoder.Encode(seed.OwnedTitleId),
            releaseId = IdCoder.Encode(seed.OwnedReleaseId),
            startedAt = "2026-10-01T10:00:00Z"
        });
        written.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await client.GetAsync("/api/me/activity/recently-played?limit=1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertTerminalPage(page.RootElement, 1);
        page.RootElement.GetProperty("items")[0].GetProperty("id").GetString().ShouldBe(IdCoder.Encode(seed.OwnedTitleId));
    }

    [Fact]
    public async Task DiscSets_TopLevelCollectionHasExplicitEmptyPageAndRetiresNestedRead()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "s6-discsets@localhost", seed.LibraryId);
        string titleId = IdCoder.Encode(seed.OwnedTitleId);

        using var response = await client.GetAsync($"/api/disc-sets?titleId={titleId}&limit=1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertTerminalPage(page.RootElement, 0);
        using var retired = await client.GetAsync($"/api/titles/{titleId}/disc-sets");
        retired.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/api/catalog?sortBy=unknown")]
    [InlineData("/api/catalog?sortBy=NAME")]
    [InlineData("/api/catalog?completeness=unknown")]
    [InlineData("/api/catalog?completeness=Complete")]
    [InlineData("/api/catalog?cursor=not-a-cursor")]
    [InlineData("/api/collections?cursor=not-a-cursor")]
    [InlineData("/api/me/activity/recently-played?cursor=not-a-cursor")]
    public async Task ConsumerCollections_InvalidFilterOrCursor_ReturnsBadRequest(string path)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "s6-invalid@localhost", seed.LibraryId);

        using var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
        problem.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("sortBy=rating")]
    [InlineData("completeness=complete")]
    [InlineData("query=chrono")]
    public async Task Catalog_ContinuationRejectsChangedFilters(string changedFilter)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        await using (var db = fixture.CreateSetupDbContext())
        {
            db.MaterializedLibraryTitles.Add(new MaterializedLibraryTitleEntity
            {
                LibraryId = seed.LibraryId,
                TitleId = seed.PartialTitleId,
                PlatformId = seed.PlatformId,
                IsVisible = true,
                IsOwned = true,
                IsPlayable = false,
                EligibleReleaseCount = 1,
                ExposedReleaseCount = 1,
                Availability = LibraryTitleAvailability.Partial.ToString()
            });
            await db.SaveChangesAsync();
        }
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "s6-cursor@localhost", seed.LibraryId);
        using var firstResponse = await client.GetAsync("/api/catalog?limit=1");
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        first.RootElement.GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;

        using var response = await client.GetAsync($"/api/catalog?limit=2&{changedFilter}&cursor={Uri.EscapeDataString(cursor)}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static void AssertTerminalPage(JsonElement page, int count)
    {
        page.EnumerateObject().Select(x => x.Name).Order().ShouldBe(["hasNextPage", "items", "nextCursor"]);
        page.GetProperty("items").GetArrayLength().ShouldBe(count);
        page.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        page.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task TitleDetail_EmbedsBoundedPagesAndMediaContinuationRejectsForeignScope()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        await using (var db = fixture.CreateSetupDbContext())
        {
            db.TitleMedia.AddRange(Enumerable.Range(0, 101).Select(index => new TitleMediaEntity
            {
                TitleId = seed.OwnedTitleId,
                FileId = seed.FileId,
                Type = "Screenshot",
                SourceId = "gallery:s6:" + index,
                IsPrimary = index % 2 == 0
            }));
            for (int index = 1; index <= 101; index++)
            {
                int releaseId = 1000 + index;
                db.CatalogReleases.Add(new CatalogReleaseEntity
                {
                    Id = releaseId,
                    CatalogTitleId = seed.OwnedTitleId,
                    PlatformId = seed.PlatformId,
                    Name = "ZZ S6 tied release",
                    Fingerprint = "s6-release:" + index
                });
                db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
                {
                    LibraryId = seed.LibraryId,
                    CatalogReleaseId = releaseId,
                    TitleId = seed.OwnedTitleId,
                    PlatformId = seed.PlatformId,
                    DatGameId = releaseId,
                    DatFileId = 1,
                    IsOwned = true,
                    IsExposed = true,
                    IsEligible = true,
                    IsComplete = true
                });
            }
            await db.SaveChangesAsync();
        }
        var userId = Guid.NewGuid();
        using var client = fixture.CreateAuthenticatedClient(userId, "s6-title-pages@localhost", seed.LibraryId);
        string titleId = IdCoder.Encode(seed.OwnedTitleId);
        using var detailResponse = await client.GetAsync($"/api/titles/{titleId}");
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        detail.RootElement.GetProperty("media").GetProperty("items").GetArrayLength().ShouldBe(50);
        detail.RootElement.GetProperty("media").GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        detail.RootElement.GetProperty("releases").GetProperty("items").GetArrayLength().ShouldBe(50);
        detail.RootElement.GetProperty("releases").GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        detail.RootElement.GetProperty("defaultReleaseId").GetString().ShouldBe(IdCoder.Encode(seed.OwnedReleaseId));

        using var firstResponse = await client.GetAsync($"/api/titles/{titleId}/media?limit=100");
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        first.RootElement.GetProperty("items").GetArrayLength().ShouldBe(100);
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        using var lastResponse = await client.GetAsync($"/api/titles/{titleId}/media?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        lastResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var last = JsonDocument.Parse(await lastResponse.Content.ReadAsStringAsync());
        AssertTerminalPage(last.RootElement, 1);
        using var wrongResource = await client.GetAsync($"/api/titles/{titleId}/releases?cursor={Uri.EscapeDataString(cursor)}");
        wrongResource.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var releasesResponse = await client.GetAsync($"/api/titles/{titleId}/releases?limit=100");
        releasesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var releases = JsonDocument.Parse(await releasesResponse.Content.ReadAsStringAsync());
        releases.RootElement.GetProperty("items").GetArrayLength().ShouldBe(100);
        string releaseCursor = releases.RootElement.GetProperty("nextCursor").GetString()!;
        using var lastReleasesResponse = await client.GetAsync($"/api/titles/{titleId}/releases?limit=100&cursor={Uri.EscapeDataString(releaseCursor)}");
        lastReleasesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var lastReleases = JsonDocument.Parse(await lastReleasesResponse.Content.ReadAsStringAsync());
        AssertTerminalPage(lastReleases.RootElement, 2);
        await fixture.ReassignUserAsync(userId, seed.OtherLibraryId);
        using var foreignScope = await client.GetAsync($"/api/titles/{titleId}/media?cursor={Uri.EscapeDataString(cursor)}");
        foreignScope.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task AddCollectionAsync(ConsumerDeliveryFixture fixture, int libraryId, int platformId)
    {
        await using var db = fixture.CreateSetupDbContext();
        var collection = new CollectionEntity { Name = "S6 second collection", PlatformId = platformId, SortOrder = 30 };
        db.Collections.Add(collection);
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        db.LibraryCollections.Add(new LibraryCollectionEntity { LibraryId = libraryId, CollectionId = collection.Id });
        db.CollectionItems.Add(new CollectionItemEntity
        {
            CollectionId = collection.Id,
            TitleId = await db.MaterializedLibraryTitles.Where(item => item.LibraryId == libraryId).Select(item => item.TitleId).FirstAsync()
        });
        await db.SaveChangesAsync();
    }
}
