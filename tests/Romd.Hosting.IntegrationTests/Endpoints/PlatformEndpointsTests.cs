using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Domain.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using System.Net.Http.Json;
using System.Text.Json;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Titles;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for Sqid-typed platform alias identity
///     (docs/decisions/api-contract-policy.md, "Opaque Public Identity" and
///     "Strict Validation"): alias ids are Sqid strings in responses, Location headers, and
///     delete routes, and a malformed alias Sqid fails with the shared 400 envelope.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PlatformEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public PlatformEndpointsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateAuthenticatedClient();
    }

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("titles")]
    [InlineData("local-payload")]
    public async Task ListTitles_BoundedPages_PreserveEqualNameOrderingAndScope(string path)
    {
        var seed = await SeedPagesAsync(106);
        using var user = _fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User], libraryId: seed.LibraryId);
        string url = $"/api/systems/snes/{path}";
        var first = await ReadPage(user, url);
        first.GetProperty("items").GetArrayLength().ShouldBe(50);
        first.GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        string cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await ReadPage(user, $"{url}?cursor={cursor}");
        second.GetProperty("items").GetArrayLength().ShouldBe(path == "titles" ? 50 : 3);
        var firstIds = first.GetProperty("items").EnumerateArray().Select(title => title.GetProperty("id").GetString()).ToArray();
        var secondIds = second.GetProperty("items").EnumerateArray().Select(title => title.GetProperty("id").GetString()).ToArray();
        firstIds.Intersect(secondIds).ShouldBeEmpty();
        var seen = firstIds.Concat(secondIds).ToList();
        if (path == "titles")
        {
            var final = await ReadPage(user, $"{url}?cursor={second.GetProperty("nextCursor").GetString()}");
            final.GetProperty("items").GetArrayLength().ShouldBe(6);
            seen.AddRange(final.GetProperty("items").EnumerateArray().Select(title => title.GetProperty("id").GetString()));
            final.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
            final.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        else
        {
            second.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
            second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        seen.ShouldBe(seed.TitleIds.Where((_, index) => path == "titles" || index % 2 == 0).Select(id => new Sqid(id).ToString()));
        (await ReadPage(user, $"{url}?limit=100")).GetProperty("items").GetArrayLength().ShouldBe(path == "titles" ? 100 : 53);
        using var otherLibrary = _fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User], libraryId: seed.LibraryId + 100000);
        await AssertBadCursor(otherLibrary, $"{url}?cursor={cursor}");
        await AssertBadCursor(_client, $"{url}?cursor={cursor}"); // contributor bypass has a different scope
        await AssertBadCursor(user, $"/api/systems/nes/{path}?cursor={cursor}");
        await AssertBadCursor(user, $"/api/systems/snes/{(path == "titles" ? "local-payload" : "titles")}?cursor={cursor}");
    }

    [Fact]
    public async Task ListTitles_MaxLengthSupplementaryNames_ContinuesThroughHttp()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 500));
        var seed = await SeedPagesAsync(2, name);
        using var user = _fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User], libraryId: seed.LibraryId);
        string url = "/api/systems/snes/titles?limit=1";
        var first = await ReadPage(user, url);
        string cursor = first.GetProperty("nextCursor").GetString()!;
        cursor.Length.ShouldBeLessThan(4096);
        var second = await ReadPage(user, $"{url}&cursor={cursor}");
        first.GetProperty("items")[0].GetProperty("id").GetString().ShouldBe(new Sqid(seed.TitleIds[0]).ToString());
        second.GetProperty("items")[0].GetProperty("id").GetString().ShouldBe(new Sqid(seed.TitleIds[1]).ToString());
        second.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("titles")]
    [InlineData("local-payload")]
    public async Task ListTitles_CursorNameContainingNul_ReturnsShared400(string path)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        int platformId = await db.Platforms.Where(row => row.CanonicalKey == "snes").Select(row => row.Id).SingleAsync();
        string cursor = PlatformTitlePageRequest.Create(platformId, null, path == "local-payload")
            .Value.CursorFor("before\0after", 42);

        await AssertBadCursor(_client, $"/api/systems/snes/{path}?cursor={cursor}");
    }

    [Theory]
    [InlineData("titles")]
    [InlineData("local-payload")]
    public async Task ListTitles_InvalidInputs_ReturnShared400AndNoLibraryReturnsEmpty(string path)
    {
        await SeedPagesAsync(1);
        foreach (string query in new[] { "cursor=", "cursor=%20", "cursor=broken", "limit=0", "limit=101", "limit=-1", "limit=abc", "limit=" })
        {
            var response = await _client.GetAsync($"/api/systems/snes/{path}?{query}");
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, query);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("errorCode").GetString().ShouldNotBeNullOrWhiteSpace();
            body.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        }
        using var unscoped = _fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        var empty = await ReadPage(unscoped, $"/api/systems/snes/{path}");
        empty.GetProperty("items").GetArrayLength().ShouldBe(0);
        empty.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        empty.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        using var missing = await _client.GetAsync($"/api/systems/missing-system/{path}");
        using var missingBody = await ProblemEnvelope.ReadAsync(missing, 404, "Systems.NotFound");
        using var anonymous = _fixture.CreateClient();
        (await anonymous.GetAsync($"/api/systems/snes/{path}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task AssertBadCursor(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().ShouldBe("PlatformTitles.InvalidCursor");
    }

    private static async Task<JsonElement> ReadPage(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task<(int LibraryId, int[] TitleIds)> SeedPagesAsync(int count, string? titleName = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        int platform = await db.Platforms.Where(row => row.CanonicalKey == "snes").Select(row => row.Id).SingleAsync();
        var library = new LibraryEntity { Name = $"Page scope {Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = Guid.NewGuid() };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        string name = titleName ?? $"Equal title {Guid.NewGuid():N}";
        var titles = Enumerable.Range(0, count).Select(index => new TitleEntity
        {
            PlatformId = platform,
            Name = name,
            NormalizedName = $"page-{Guid.NewGuid():N}-{index}",
            EnrichmentStatus = "None",
            HasLocalPayload = index % 2 == 0,
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        }).ToList();
        db.Titles.AddRange(titles);
        await db.SaveChangesAsync();
        db.MaterializedLibraryTitles.AddRange(titles.Select(title => new MaterializedLibraryTitleEntity
        {
            LibraryId = library.Id,
            TitleId = title.Id,
            PlatformId = platform,
            ExposedReleaseCount = 1,
            Availability = "Available"
        }));
        await db.SaveChangesAsync();
        return (library.Id, titles.Select(title => title.Id).ToArray());
    }

    [Theory]
    [InlineData("aliases")]
    [InlineData("bios")]
    public async Task SystemRelationship_PageTraversal_IsCompleteAndRejectsAnotherSystemsCursor(string relationship)
    {
        string key = "local-s6-" + Guid.NewGuid().ToString("N");
        var expected = new HashSet<string>(StringComparer.Ordinal);
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var system = new PlatformEntity
            {
                CanonicalKey = key,
                Name = key,
                BaseName = key,
                BaseCompactLabel = "S6",
                ShortName = key,
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation
            };
            db.Platforms.Add(system);
            await db.SaveChangesAsync();
            for (int index = 0; index < 103; index++)
            {
                string name = key + "-" + index;
                if (relationship == "aliases")
                    db.PlatformAliases.Add(new()
                    {
                        PlatformId = system.Id,
                        Type = Romd.Domain.Source.Platform.PlatformAliasType.Name,
                        Value = name,
                        NormalizedValue = name
                    });
                else
                    db.Bios.Add(new() { PlatformId = system.Id, Name = name, NormalizedName = name });
            }
            await db.SaveChangesAsync();
            var ids = relationship == "aliases" ? await db.PlatformAliases.Where(x => x.PlatformId == system.Id).Select(x => x.Id).ToListAsync()
                : await db.Bios.Where(x => x.PlatformId == system.Id).Select(x => x.Id).ToListAsync();
            expected.UnionWith(ids.Select(IdCoder.Encode));
        }
        string root = $"/api/systems/{key}/{relationship}?limit=50";
        var seen = new List<string>();
        string? cursor = null;
        string? firstCursor = null;
        do
        {
            var page = await ReadPage(_client, root + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            bool more = page.GetProperty("hasNextPage").GetBoolean();
            cursor = page.GetProperty("nextCursor").GetString();
            if (more)
            { cursor.ShouldNotBeNullOrWhiteSpace(); firstCursor ??= cursor; }
            else
                cursor.ShouldBeNull();
        } while (cursor != null);
        seen.Count.ShouldBe(103);
        seen.Distinct(StringComparer.Ordinal).Count().ShouldBe(103);
        seen.ToHashSet(StringComparer.Ordinal).SetEquals(expected).ShouldBeTrue();
        using var wrongScope = await _client.GetAsync($"/api/systems/nes/{relationship}?cursor={Uri.EscapeDataString(firstCursor!)}");
        using var problem = await ProblemEnvelope.ReadAsync(wrongScope, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").GetProperty("cursor").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task AddAlias_ReturnsSqidIdAndSqidLocation()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.PostAsJsonAsync(
            $"/api/systems/{platformId}/aliases",
            new { type = "name", value = $"Alias {Guid.NewGuid():N}" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string aliasId = doc.RootElement.GetProperty("id").GetString().ShouldNotBeNull();
        IdCoder.TryDecode(aliasId, out _).ShouldBeTrue($"'{aliasId}' should be a Sqid");
        response.Headers.Location.ShouldNotBeNull()
            .OriginalString.ShouldBe($"/api/systems/{platformId}/aliases/{aliasId}");
    }

    [Fact]
    public async Task RemoveAlias_ViaReturnedSqidId_Returns204()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);
        var createResponse = await _client.PostAsJsonAsync(
            $"/api/systems/{platformId}/aliases",
            new { type = "name", value = $"Alias {Guid.NewGuid():N}" });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        string aliasId = created.RootElement.GetProperty("id").GetString().ShouldNotBeNull();

        var deleteResponse = await _client.DeleteAsync($"/api/systems/{platformId}/aliases/{aliasId}");

        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemoveAlias_MalformedAliasSqid_Returns400Envelope()
    {
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.DeleteAsync($"/api/systems/{platformId}/aliases/not-a-sqid");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidParameter");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errors").TryGetProperty("aliasId", out _).ShouldBeTrue();
    }
}
