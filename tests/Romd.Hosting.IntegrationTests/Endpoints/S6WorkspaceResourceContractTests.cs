using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Hosting.IntegrationTests.Hosting;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class S6WorkspaceResourceContractTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData("GET", "/api/users/directory")]
    [InlineData("GET", "/api/tracked-titles/missing")]
    [InlineData("GET", "/api/tracked-titles/satisfied")]
    [InlineData("GET", "/api/tracked-titles/upgrades")]
    [InlineData("GET", "/api/dats/unrouted")]
    [InlineData("GET", "/api/catalog/count")]
    [InlineData("PUT", "/api/libraries/{libraryId}")]
    [InlineData("POST", "/api/libraries/{libraryId}/evaluate")]
    [InlineData("GET", "/api/libraries/collection-placements/{collectionId}")]
    [InlineData("PUT", "/api/collections/{collectionId}/items/reorder")]
    [InlineData("PATCH", "/api/dats/{datId}/platform")]
    public void RetiredWorkspaceRoute_IsNoLongerMapped(string method, string route)
    {
        AuthorizationInventory.GetRouteEndpoints(fixture.Services).ShouldNotContain(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
    }

    [Theory]
    [InlineData("/api/users?limit=1")]
    [InlineData("/api/libraries?limit=1")]
    [InlineData("/api/collections?limit=1")]
    [InlineData("/api/dats?limit=1")]
    [InlineData("/api/catalog?limit=1")]
    [InlineData("/api/tracked-titles?status=missing&limit=1")]
    public async Task GrowableCollection_CanonicalRoute_ReturnsOnlyTheCommonPage(string path)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertPage(body.RootElement, 1);
    }

    [Theory]
    [InlineData("/api/users?role=not-a-role", "role")]
    [InlineData("/api/users?status=not-a-status", "status")]
    [InlineData("/api/users?libraryId=not-an-id", "libraryId")]
    [InlineData("/api/users?limit=0", "limit")]
    [InlineData("/api/libraries?limit=101", "limit")]
    [InlineData("/api/collections?limit=0", "limit")]
    [InlineData("/api/dats?limit=101", "limit")]
    [InlineData("/api/tracked-titles?status=not-a-status", "status")]
    [InlineData("/api/tracked-titles?limit=0", "limit")]
    public async Task CollectionQuery_SuppliedInvalidValue_IsAnAttributedProblem(string path, string field)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(path);

        await AssertParameterProblemAsync(response, field);
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/libraries")]
    [InlineData("/api/collections")]
    [InlineData("/api/dats")]
    [InlineData("/api/tracked-titles?status=missing")]
    public async Task CollectionCursor_MalformedDefaultAndOversized_AreAttributedProblems(string path)
    {
        using var client = fixture.CreateAuthenticatedClient();
        foreach (string cursor in new[] { "", "not-a-cursor", "e30", new string('a', 5000) })
        {
            using var response = await client.GetAsync(AddQuery(path, "cursor", cursor));

            await AssertParameterProblemAsync(response, "cursor");
        }
    }

    [Fact]
    public async Task Users_QueryPagesTraverseEveryMatchAndRejectAChangedFilter()
    {
        string prefix = $"s6-directory-{Guid.NewGuid():N}";
        var ids = await SeedUsersAsync(prefix, 53);
        using var client = fixture.CreateAuthenticatedClient();
        string path = $"/api/users?query={prefix}&limit=17";

        var rows = await ReadAllAsync(client, path);

        rows.Select(row => row.GetProperty("id").GetGuid()).ShouldBe(ids, ignoreOrder: true);
        using var first = await ReadPageAsync(client, path);
        string cursor = first.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        foreach (string incompatible in new[]
                 {
                     $"/api/users?query={prefix}-other&limit=17",
                     $"/api/users?query={prefix}&status=Suspended&limit=17"
                 })
        {
            using var response = await client.GetAsync(AddQuery(incompatible, "cursor", cursor));
            await AssertParameterProblemAsync(response, "cursor");
        }
    }

    [Fact]
    public async Task Audit_LongIdsRemainOpaqueAndCursorIsBoundToItsTarget()
    {
        string target = Guid.NewGuid().ToString("D");
        long[] internalIds = [(long)uint.MaxValue + 1001, (long)uint.MaxValue + 1002];
        await InDatabaseAsync(async db =>
        {
            db.AdminAuditEvents.AddRange(internalIds.Select(id => new AdminAuditEventEntity
            {
                Id = id,
                ActorId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                Action = "S6 audit identity fixture",
                TargetType = "S6Contract",
                TargetId = target
            }));
            await db.SaveChangesAsync();
        });
        using var client = fixture.CreateAuthenticatedClient();
        string path = $"/api/audit?targetType=S6Contract&targetId={target}&limit=1";

        var rows = await ReadAllAsync(client, path);

        string[] publicIds = rows.Select(row => row.GetProperty("id").GetString()!).ToArray();
        publicIds.Length.ShouldBe(2);
        publicIds.Distinct().Count().ShouldBe(2);
        foreach (string id in publicIds)
        {
            id.ShouldNotBeNullOrWhiteSpace();
            internalIds.Select(value => value.ToString(CultureInfo.InvariantCulture)).ShouldNotContain(id);
            internalIds.Select(value => IdCoder.Encode(unchecked((int)value))).ShouldNotContain(id);
        }
        using var first = await ReadPageAsync(client, path);
        string cursor = first.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        using var changed = await client.GetAsync(AddQuery(
            $"/api/audit?targetType=S6Contract&targetId={Guid.NewGuid():D}&limit=1", "cursor", cursor));
        await AssertParameterProblemAsync(changed, "cursor");
    }

    [Fact]
    public async Task AccountLinks_CreationHasCanonicalLocationAndHistoryIsCompleteWithoutSecrets()
    {
        Guid userId = (await SeedUsersAsync($"s6-links-{Guid.NewGuid():N}", 1))[0];
        await InDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.AccountLinks.AddRange(Enumerable.Range(0, 24).Select(index => new AccountLinkEntity
            {
                UserId = userId,
                Purpose = "recovery",
                TokenHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                CreatedAt = now.AddMinutes(-index - 1),
                ExpiresAt = now.AddHours(1),
                RevokedAt = now
            }));
            await db.SaveChangesAsync();
        });
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync($"/api/users/{userId:D}/links", new { purpose = "recovery" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string linkId = created.RootElement.GetProperty("id").GetString().ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe($"/api/users/{userId:D}/links/{linkId}");
        created.RootElement.GetProperty("token").GetString().ShouldNotBeNullOrWhiteSpace();
        var rows = await ReadAllAsync(client, $"/api/users/{userId:D}/links?limit=7");
        rows.Count.ShouldBe(25);
        rows.Select(row => row.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(25);
        foreach (var row in rows)
        {
            row.TryGetProperty("token", out _).ShouldBeFalse();
            row.TryGetProperty("tokenHash", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task UserSessions_PublicPageTraversesTheWholeHistoryAndSecurityEmbedsPages()
    {
        Guid userId = (await SeedUsersAsync($"s6-sessions-{Guid.NewGuid():N}", 1))[0];
        Guid[] sessionIds = [];
        await InDatabaseAsync(async db =>
        {
            var created = DateTimeOffset.UtcNow.AddDays(-2);
            var sessions = Enumerable.Range(0, 205).Select(_ => new AccountSessionEntity
            {
                UserId = userId,
                ClientId = "romd-console",
                Device = "S6 device",
                AccountStamp = "private-account-stamp",
                CreatedAt = created,
                LastUsedAt = created,
                ExpiresAt = created.AddDays(1),
                RevokedAt = created.AddHours(1)
            }).ToArray();
            db.AccountSessions.AddRange(sessions);
            await db.SaveChangesAsync();
            sessionIds = sessions.Select(session => session.Id).ToArray();
        });
        using var client = fixture.CreateAuthenticatedClient();

        var rows = await ReadAllAsync(client, $"/api/users/{userId:D}/sessions?limit=67");

        rows.Select(row => row.GetProperty("id").GetGuid()).ShouldBe(sessionIds, ignoreOrder: true);
        foreach (var row in rows)
            row.TryGetProperty("accountStamp", out _).ShouldBeFalse();
        using var security = await ReadPageAsync(client, $"/api/users/{userId:D}/security");
        AssertPage(security.RootElement.GetProperty("links"), 100);
        AssertPage(security.RootElement.GetProperty("sessions"), 100);
        security.RootElement.GetProperty("sessions").GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Library_CreationPublishesLocationAndPartialUpdateUsesPatch()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string name = $"S6 Library {Guid.NewGuid():N}";
        using var created = await client.PostAsJsonAsync("/api/libraries", new { name });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var library = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        string id = library.RootElement.GetProperty("id").GetString().ShouldNotBeNull();
        created.Headers.Location!.OriginalString.ShouldBe($"/api/libraries/{id}");

        using var updated = await client.PatchAsJsonAsync($"/api/libraries/{id}", new { name = name + " updated" });

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("name").GetString().ShouldBe(name + " updated");
        body.RootElement.GetProperty("configuration").GetRawText()
            .ShouldBe(library.RootElement.GetProperty("configuration").GetRawText());
    }

    [Fact]
    public async Task LibraryRelationships_UseCommonPagesAndConfigurationPreviewEmbedsOne()
    {
        string libraryId = "";
        int pendingPlatformId = 0;
        await InDatabaseAsync(async db =>
        {
            var library = new LibraryEntity { Name = $"S6 Relationship Library {Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
            string suffix = Guid.NewGuid().ToString("N")[..12];
            var pendingPlatform = new PlatformEntity
            {
                Name = $"S6 Preview Pending {suffix}",
                ShortName = $"s6prev-{suffix}",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation,
                CanonicalKey = $"local-s6-preview-{suffix}",
                BaseName = $"S6 Preview Pending {suffix}",
                BaseCompactLabel = "S6 preview",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = Romd.Domain.Identity.SystemActor.UserId,
                CatalogRebuildState = CatalogRebuildState.Dirty
            };
            db.Libraries.Add(library);
            db.Platforms.Add(pendingPlatform);
            await db.SaveChangesAsync();
            libraryId = IdCoder.Encode(library.Id);
            pendingPlatformId = pendingPlatform.Id;
        });
        var collection = await SeedCollectionAsync(1);
        using var client = fixture.CreateAuthenticatedClient();
        foreach (string path in new[]
                 {
                     $"/api/libraries/{libraryId}/collections?limit=1",
                     $"/api/libraries/{libraryId}/preview?limit=1",
                     $"/api/libraries/{libraryId}/facets/genres?limit=1",
                     $"/api/collections/{collection.CollectionId}/library-placements?limit=1"
                 })
        {
            using var body = await ReadPageAsync(client, path);
            AssertPage(body.RootElement, 1);
        }

        using (var blocked = await client.PostAsJsonAsync($"/api/libraries/{libraryId}/configuration/preview",
                   new { configuration = new { }, view = "matching", limit = 1 }))
        using (var problem = await ProblemEnvelope.ReadAsync(blocked, 409, "Libraries.CatalogUpdating"))
            problem.RootElement.GetProperty("detail").GetString()
                .ShouldBe("The catalog is updating. Try evaluating again when it finishes.");

        // Other tests share this catalog, while the fixture omits the production recovery dispatcher.
        // A configuration comparison requires its real catalog projection to be current globally.
        using (var scope = fixture.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogProjectionService>();
            var pendingPlatformIds = await catalog.GetPlatformIdsNeedingRebuildAsync();
            pendingPlatformIds.ShouldContain(pendingPlatformId);
            foreach (int platformId in pendingPlatformIds)
                (await catalog.RebuildPlatformAsync(platformId)).ShouldBeTrue(
                    $"Catalog platform {platformId} must converge before evaluating the library configuration.");
            (await catalog.GetPlatformIdsNeedingRebuildAsync()).ShouldBeEmpty();
        }

        using var response = await client.PostAsJsonAsync($"/api/libraries/{libraryId}/configuration/preview",
            new { configuration = new { }, view = "matching", limit = 1 });

        string responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, responseBody);
        using var preview = JsonDocument.Parse(responseBody);
        AssertPage(preview.RootElement.GetProperty("page"), 1);
        preview.RootElement.TryGetProperty("nextCursor", out _).ShouldBeFalse();
        preview.RootElement.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("collections")]
    [InlineData("preview")]
    [InlineData("facets/genres")]
    public async Task LibraryRelationship_UnknownLibrary_IsAnEnvelope(string relationship)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/libraries/{IdCoder.Encode(int.MaxValue)}/{relationship}");

        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Libraries.NotFound");
    }

    [Theory]
    [InlineData("GET", "/api/users/{userId}/links")]
    [InlineData("GET", "/api/users/{userId}/sessions")]
    [InlineData("GET", "/api/users/{userId}/security")]
    [InlineData("DELETE", "/api/users/{userId}/sessions")]
    public async Task UserScopedResource_UnknownUser_ReturnsAnEnvelope(string method, string path)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method),
            path.Replace("{userId}", Guid.NewGuid().ToString("D"))));

        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Users.NotFound");
    }

    [Fact]
    public async Task CollectionItems_EmbeddedFirstPageAndFeedTraverseTiedOrdersWithoutLoss()
    {
        var seeded = await SeedCollectionAsync(105);
        using var client = fixture.CreateAuthenticatedClient();
        using var detail = await ReadPageAsync(client, $"/api/collections/{seeded.CollectionId}");

        var embedded = detail.RootElement.GetProperty("items");
        AssertPage(embedded, 100);
        embedded.GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        var rows = await ReadAllAsync(client, $"/api/collections/{seeded.CollectionId}/items?limit=31");
        rows.Select(row => row.GetProperty("titleId").GetString()).ShouldBe(seeded.TitleIds, ignoreOrder: true);
        using var first = await ReadPageAsync(client, $"/api/collections/{seeded.CollectionId}/items?limit=31");
        using var other = await client.GetAsync(AddQuery($"/api/collections/{IdCoder.Encode(int.MaxValue)}/items?limit=31",
            "cursor", first.RootElement.GetProperty("nextCursor").GetString()!));
        using var problem = await ProblemEnvelope.ReadAsync(other);
        ((int)other.StatusCode).ShouldBeOneOf(400, 404);
    }

    [Fact]
    public async Task CollectionItem_CreateHasMembershipLocationAndPartialReorderCannotDropUnseenMembers()
    {
        var seeded = await SeedCollectionAsync(3);
        var extra = await SeedTitleAsync();
        using var client = fixture.CreateAuthenticatedClient();

        using var created = await client.PostAsJsonAsync($"/api/collections/{seeded.CollectionId}/items", new { titleId = extra });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.OriginalString.ShouldBe($"/api/collections/{seeded.CollectionId}/items/{extra}");
        using var membership = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        membership.RootElement.GetProperty("titleId").GetString().ShouldBe(extra);
        using var reorder = await client.PutAsJsonAsync($"/api/collections/{seeded.CollectionId}/item-order",
            new { titleIds = new[] { seeded.TitleIds[0], extra } });
        ((int)reorder.StatusCode).ShouldBeOneOf(400, 409);
        using var problem = await ProblemEnvelope.ReadAsync(reorder);
        var rows = await ReadAllAsync(client, $"/api/collections/{seeded.CollectionId}/items?limit=2");
        rows.Select(row => row.GetProperty("titleId").GetString()).ShouldBe(seeded.TitleIds.Append(extra), ignoreOrder: true);
    }

    [Fact]
    public async Task TitleMedia_EmbeddedPageAndFeedShareTheTitleValidatorAndRejectCrossTitleCursor()
    {
        string titleId = await SeedTitleAsync();
        await InDatabaseAsync(async db =>
        {
            var file = NewFile();
            db.Files.Add(file);
            await db.SaveChangesAsync();
            db.TitleMedia.AddRange(Enumerable.Range(0, 105).Select(index => new TitleMediaEntity
            {
                TitleId = Sqid.Parse(titleId, null).Value,
                Type = "Screenshot",
                FileId = file.Id,
                SourceId = "user",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index)
            }));
            await db.SaveChangesAsync();
        });
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/titles/{titleId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertPage(detail.RootElement.GetProperty("media"), 100);
        detail.RootElement.GetProperty("media").GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        var media = await ReadAllAsync(client, $"/api/titles/{titleId}/media?limit=31");
        media.Select(row => row.GetProperty("id").GetString()).Distinct().Count().ShouldBe(105);
        using var firstResponse = await client.GetAsync($"/api/titles/{titleId}/media?limit=31");
        firstResponse.Headers.ETag!.Tag.ShouldBe(response.Headers.ETag!.Tag);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        string otherTitle = await SeedTitleAsync();
        using var crossed = await client.GetAsync(AddQuery($"/api/titles/{otherTitle}/media?limit=31", "cursor",
            first.RootElement.GetProperty("nextCursor").GetString()!));
        await AssertParameterProblemAsync(crossed, "cursor");
    }

    [Fact]
    public async Task TitleReleases_PagesKeepCanonicalGroupsAndCompleteSourceProvenance()
    {
        var seeded = await SeedReleaseGraphAsync();
        using var client = fixture.CreateAuthenticatedClient();
        using var detail = await ReadPageAsync(client, $"/api/titles/{seeded.TitleId}");

        AssertPage(detail.RootElement.GetProperty("releases"), 100);
        detail.RootElement.GetProperty("releases").GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        var releases = await ReadAllAsync(client, $"/api/titles/{seeded.TitleId}/releases?limit=31");
        releases.Count.ShouldBe(105);
        releases.Select(row => row.GetProperty("id").GetString()).Distinct().Count().ShouldBe(105);
        var grouped = releases.Single(row => row.GetProperty("id").GetString() == seeded.RepresentativeId);
        AssertPage(grouped.GetProperty("sources"), 100);
        grouped.GetProperty("sources").GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        var sources = await ReadAllAsync(client,
            $"/api/titles/{seeded.TitleId}/release-sources?releaseId={seeded.RepresentativeId}&limit=31");
        sources.Select(row => row.GetProperty("datGameId").GetString()).ShouldBe(seeded.SourceIds, ignoreOrder: true);
        using var first = await ReadPageAsync(client,
            $"/api/titles/{seeded.TitleId}/release-sources?releaseId={seeded.RepresentativeId}&limit=31");
        string otherRelease = releases.First(row => row.GetProperty("id").GetString() != seeded.RepresentativeId)
            .GetProperty("id").GetString()!;
        using var changed = await client.GetAsync(AddQuery(
            $"/api/titles/{seeded.TitleId}/release-sources?releaseId={otherRelease}&limit=31", "cursor",
            first.RootElement.GetProperty("nextCursor").GetString()!));
        await AssertParameterProblemAsync(changed, "cursor");
    }

    [Theory]
    [InlineData("media?cursor=not-a-cursor")]
    [InlineData("releases?cursor=not-a-cursor")]
    [InlineData("release-sources?releaseId=not-an-id&cursor=not-a-cursor")]
    public async Task TitleRelationship_UnknownTitleTakesPrecedenceOverRelationshipValidation(string suffix)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/titles/{IdCoder.Encode(int.MaxValue)}/{suffix}");

        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Catalog.TitleNotFound");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain(int.MaxValue.ToString());
    }

    [Fact]
    public async Task TitleReleaseSources_UnknownOrForeignReleaseIs404RatherThanEmptySuccess()
    {
        string title = await SeedTitleAsync();
        var foreign = await SeedReleaseGraphAsync();
        using var client = fixture.CreateAuthenticatedClient();
        foreach (string release in new[] { IdCoder.Encode(int.MaxValue), foreign.RepresentativeId })
        {
            using var response = await client.GetAsync($"/api/titles/{title}/release-sources?releaseId={release}");
            using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Catalog.ReleaseNotFound");
        }
    }

    private static void AssertPage(JsonElement page, int maxItems)
    {
        page.ValueKind.ShouldBe(JsonValueKind.Object);
        page.EnumerateObject().Select(property => property.Name)
            .ShouldBe(new[] { "items", "nextCursor", "hasNextPage" }, ignoreOrder: true);
        page.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);
        page.GetProperty("items").GetArrayLength().ShouldBeLessThanOrEqualTo(maxItems);
        bool hasNext = page.GetProperty("hasNextPage").GetBoolean();
        if (hasNext)
            page.GetProperty("nextCursor").GetString().ShouldNotBeNullOrWhiteSpace();
        else
            page.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static async Task AssertParameterProblemAsync(HttpResponseMessage response, string field)
    {
        ((int)response.StatusCode).ShouldBe(400);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldContain(field);
    }

    private static async Task<JsonDocument> ReadPageAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<JsonElement>> ReadAllAsync(HttpClient client, string path)
    {
        var rows = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int pageNumber = 0; pageNumber < 120; pageNumber++)
        {
            using var page = await ReadPageAsync(client, cursor is null ? path : AddQuery(path, "cursor", cursor));
            AssertPage(page.RootElement, 100);
            rows.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(row => row.Clone()));
            if (!page.RootElement.GetProperty("hasNextPage").GetBoolean())
                return rows;
            cursor = page.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNull();
            seen.Add(cursor).ShouldBeTrue("A continuation must advance rather than repeat a previous page.");
        }
        throw new ShouldAssertException("The finite fixture did not reach its last page.");
    }

    private static string AddQuery(string path, string name, string value) =>
        $"{path}{(path.Contains('?') ? '&' : '?')}{name}={Uri.EscapeDataString(value)}";

    private async Task InDatabaseAsync(Func<RomdDbContext, Task> action)
    {
        using var scope = fixture.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<RomdDbContext>());
    }

    private async Task<Guid[]> SeedUsersAsync(string prefix, int count)
    {
        Guid[] ids = [];
        await InDatabaseAsync(async db =>
        {
            var users = Enumerable.Range(0, count).Select(index =>
            {
                string email = $"{prefix}-{index:D3}@example.test";
                return new RomdUser
                {
                    Id = Guid.NewGuid(),
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    UserName = email,
                    NormalizedUserName = email.ToUpperInvariant(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    SecurityStamp = Guid.NewGuid().ToString(),
                    ConcurrencyStamp = Guid.NewGuid().ToString()
                };
            }).ToArray();
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            ids = users.Select(user => user.Id).ToArray();
        });
        return ids;
    }

    private async Task<string> SeedTitleAsync()
    {
        string id = "";
        await InDatabaseAsync(async db =>
        {
            var title = NewTitle(await db.Platforms.OrderBy(system => system.Id).Select(system => system.Id).FirstAsync());
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            id = IdCoder.Encode(title.Id);
        });
        return id;
    }

    private async Task<(string CollectionId, string[] TitleIds)> SeedCollectionAsync(int count)
    {
        string collectionId = "";
        string[] titleIds = [];
        await InDatabaseAsync(async db =>
        {
            int systemId = await db.Platforms.OrderBy(system => system.Id).Select(system => system.Id).FirstAsync();
            var titles = Enumerable.Range(0, count).Select(_ => NewTitle(systemId)).ToArray();
            var collection = new CollectionEntity { Name = $"S6 Collection {Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
            db.Titles.AddRange(titles);
            db.Collections.Add(collection);
            await db.SaveChangesAsync();
            db.CollectionItems.AddRange(titles.Select(title => new CollectionItemEntity
            {
                CollectionId = collection.Id,
                TitleId = title.Id,
                SortOrder = 0,
                AddedAt = DateTimeOffset.UtcNow
            }));
            await db.SaveChangesAsync();
            collectionId = IdCoder.Encode(collection.Id);
            titleIds = titles.Select(title => IdCoder.Encode(title.Id)).ToArray();
        });
        return (collectionId, titleIds);
    }

    private async Task<(string TitleId, string RepresentativeId, string[] SourceIds)> SeedReleaseGraphAsync()
    {
        string titleId = "";
        string representativeId = "";
        string[] sourceIds = [];
        await InDatabaseAsync(async db =>
        {
            int systemId = await db.Platforms.OrderBy(system => system.Id).Select(system => system.Id).FirstAsync();
            var title = NewTitle(systemId);
            var file = NewFile();
            var catalogSource = new CatalogSourceEntity { Kind = nameof(CatalogSourceKind.Dat), Status = nameof(CatalogSourceStatus.Active) };
            var dat = new DatFileEntity
            {
                Name = $"S6 Provenance {Guid.NewGuid():N}",
                Description = "S6 provenance fixture",
                Type = nameof(DatType.NoIntro),
                OriginalFilename = "s6-fixture.dat",
                PlatformId = systemId,
                Source = new DatSourceEntity { CatalogSource = catalogSource }
            };
            db.Titles.Add(title);
            db.Files.Add(file);
            await db.SaveChangesAsync();
            dat.FileId = file.Id;
            db.DatFiles.Add(dat);
            await db.SaveChangesAsync();
            var groups = Enumerable.Range(0, 105).Select(index => new CatalogReleaseEntity
            {
                PlatformId = systemId,
                CatalogTitleId = title.Id,
                Name = $"Release {index:D3}",
                Fingerprint = Guid.NewGuid().ToString("N"),
                SourceCount = index == 0 ? 105 : 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ToArray();
            db.CatalogReleases.AddRange(groups);
            await db.SaveChangesAsync();
            var pending = new List<(CatalogReleaseEntity Group, SourceEntryEntity Entry)>();
            foreach (var group in groups)
            {
                int members = group == groups[0] ? 105 : 1;
                for (int index = 0; index < members; index++)
                {
                    var entry = new SourceEntryEntity
                    {
                        CatalogSourceId = catalogSource.Id,
                        EntryKey = Guid.NewGuid().ToString("N"),
                        Name = group.Name,
                        PlatformId = systemId
                    };
                    pending.Add((group, entry));
                }
            }
            db.SourceEntries.AddRange(pending.Select(value => value.Entry));
            await db.SaveChangesAsync();
            var games = pending.Select(value => new DatGameEntity
            {
                DatFileId = dat.Id,
                SourceEntryId = value.Entry.Id,
                Name = value.Group.Name,
                CreatedAt = DateTimeOffset.UtcNow
            }).ToArray();
            db.DatGames.AddRange(games);
            db.TitleSourceLinks.AddRange(pending.Select(value => new TitleSourceLinkEntity { SourceEntryId = value.Entry.Id, TitleId = title.Id }));
            await db.SaveChangesAsync();
            db.CatalogReleaseSources.AddRange(games.Select((game, index) => new CatalogReleaseSourceEntity
            {
                CatalogReleaseId = pending[index].Group.Id,
                SourceEntryId = game.SourceEntryId,
                ProviderClaimKey = game.Id.ToString(CultureInfo.InvariantCulture),
                AssertedTitleId = title.Id
            }));
            await db.SaveChangesAsync();
            var groupedGames = games.Where((_, index) => pending[index].Group == groups[0]).ToArray();
            titleId = IdCoder.Encode(title.Id);
            representativeId = IdCoder.Encode(groupedGames.Min(game => game.Id));
            sourceIds = groupedGames.Select(game => IdCoder.Encode(game.Id)).ToArray();
        });
        return (titleId, representativeId, sourceIds);
    }

    private static TitleEntity NewTitle(int systemId) => new()
    {
        PlatformId = systemId,
        Name = $"S6 Title {Guid.NewGuid():N}",
        NormalizedName = $"s6 title {Guid.NewGuid():N}",
        EnrichmentStatus = "None",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static FileEntityPersistence NewFile() => new()
    {
        Sha256 = Sha256.FromBytes(RandomNumberGenerator.GetBytes(Sha256.ByteLength)),
        Size = 1,
        SizeOnDisk = 1
    };
}
