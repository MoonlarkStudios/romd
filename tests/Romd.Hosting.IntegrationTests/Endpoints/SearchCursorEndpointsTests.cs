using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Pagination;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.ReferenceData;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class SearchCursorEndpointsTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData("/api/search/games")]
    [InlineData("/api/catalog")]
    public async Task Search_MalformedOversizeUnboundOrInvalidPositionCursor_Returns400FieldEnvelope(string route)
    {
        using var client = fixture.CreateAuthenticatedClient();
        string fingerprint = route == "/api/catalog"
            ? SearchCursors.TitlesFingerprint(null, new(), TitleSortField.Name, null)
            : SearchCursors.GamesFingerprint(null, new(), GameSortField.Name, null);
        string invalidPosition = route == "/api/catalog"
            ? SearchCursors.EncodeTitles(new((int)TitleSortField.Name, "A", 0), fingerprint)
            : SearchCursors.EncodeGames(new(GameSortField.Name, "A", 0), fingerprint);
        foreach (string cursor in new[] { "", " ", "not-base64-json", "e30", "bnVsbA", new string('a', SearchCursors.MaxLength + 1),
                     CursorUtils.ToCursor(new CompositeCursor(GameSortField.Name, "A", 1)), invalidPosition, invalidPosition + "=" })
        {
            using var response = await client.GetAsync(route + "?cursor=" + Uri.EscapeDataString(cursor));
            await AssertCursorError(response);
        }
    }

    [Theory]
    [InlineData("/api/search/games", "year", "manufacturer=Other")]
    [InlineData("/api/catalog", "rating", "genre=Other")]
    public async Task Search_ChangedQueryFilterSortLibraryOrFamily_Returns400WithoutRestart(string route, string otherSort, string otherFilter)
    {
        var seed = await SeedAsync("Cursor Same Name");
        using var client = fixture.CreateAuthenticatedClient();
        string firstUrl = route + "?systemKey=" + seed.Key + "&limit=1";
        using var first = await client.GetAsync(firstUrl);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        string cursor = firstBody.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        string continuation = firstUrl + "&cursor=" + Uri.EscapeDataString(cursor);
        foreach (string changed in new[] { "&query=different", "&sortBy=" + otherSort, "&" + otherFilter })
        {
            using var response = await client.GetAsync(continuation + changed);
            await AssertCursorError(response);
        }
        using var narrowed = fixture.CreateClient().WithTestUser(Guid.NewGuid(), "cursor@example.test", [RomdRoleType.User], libraryId: int.MaxValue);
        using (var response = await narrowed.GetAsync(continuation))
            await AssertCursorError(response);

        string otherRoute = route == "/api/catalog" ? "/api/search/games" : "/api/catalog";
        using (var response = await client.GetAsync(otherRoute + "?systemKey=" + seed.Key + "&cursor=" + Uri.EscapeDataString(cursor)))
            await AssertCursorError(response);

        // The original scope still continues, including a changed page size; it never replays the first row.
        using var next = await client.GetAsync(continuation.Replace("limit=1", "limit=2", StringComparison.Ordinal));
        next.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var nextBody = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        var rows = nextBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        rows.ShouldHaveSingleItem().GetProperty("id").GetString().ShouldNotBe(firstBody.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        nextBody.RootElement.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        nextBody.RootElement.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Search_MaximumUnicodeName_ActualGeneratedContinuationsFitHttpLineAndKeepTieIds()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F4BF", 500));
        var seed = await SeedAsync(name);
        using var client = fixture.CreateAuthenticatedClient();
        foreach (string route in new[] { "/api/search/games", "/api/catalog" })
        {
            string url = route + "?systemKey=" + seed.Key + "&limit=1";
            using var first = await client.GetAsync(url);
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            string cursor = body.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNull();
            string target = url + "&cursor=" + Uri.EscapeDataString(cursor);
            Encoding.UTF8.GetByteCount("GET " + target + " HTTP/1.1\r\n").ShouldBeLessThan(8192);
            cursor.Length.ShouldBeLessThan(SearchCursors.MaxLength);
            using var next = await client.GetAsync(target);
            next.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var nextBody = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
            nextBody.RootElement.GetProperty("items").GetArrayLength().ShouldBe(1);
            nextBody.RootElement.GetProperty("items")[0].GetProperty("name").GetString().ShouldBe(name);
            nextBody.RootElement.GetProperty("items")[0].GetProperty("id").GetString().ShouldNotBe(body.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
            nextBody.RootElement.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("sortBy=")]
    [InlineData("sortBy=%20")]
    [InlineData("bios=")]
    [InlineData("bios=%20")]
    public async Task Games_ExplicitBlankVocabulary_DoesNotSelectDefault(string query)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/search/games?" + query);
        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Search.InvalidQuery");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(x => x.Name).ShouldBe([query.Split('=')[0]]);
    }

    private static async Task AssertCursorError(HttpResponseMessage response)
    {
        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Search.InvalidCursor");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(x => x.Name).ShouldBe(["cursor"]);
    }

    private async Task<(string Key, int PlatformId)> SeedAsync(string name)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var now = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        string key = "cursor-" + Guid.NewGuid().ToString("N");
        var platform = new PlatformEntity { Name = key, ShortName = key, CanonicalKey = key, Ownership = ReferenceOwnership.Installation, BaseName = key, BaseCompactLabel = key, CreatedAt = now };
        var source = new CatalogSourceEntity { Kind = "Dat", Status = "Active", CreatedAt = now };
        var file = new FileEntityPersistence { Sha256 = Sha256.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(key))), Size = 1, SizeOnDisk = 1, CreatedAt = now };
        db.AddRange(platform, source, file);
        await db.SaveChangesAsync();
        var datSource = new DatSourceEntity { CatalogSourceId = source.Id, CreatedAt = now };
        db.DatSources.Add(datSource);
        await db.SaveChangesAsync();
        var dat = new DatFileEntity { DatSourceId = datSource.Id, PlatformId = platform.Id, Name = key, Description = key, Type = "NoIntro", OriginalFilename = "cursor.dat", FileId = file.Id, CreatedAt = now };
        db.DatFiles.Add(dat);
        await db.SaveChangesAsync();
        for (int i = 0; i < 2; i++)
        {
            var entry = new SourceEntryEntity { CatalogSourceId = source.Id, PlatformId = platform.Id, EntryKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Name = name, CreatedAt = now };
            db.SourceEntries.Add(entry);
            db.Titles.Add(new TitleEntity { PlatformId = platform.Id, Name = name, NormalizedName = "cursor" + i, EnrichmentStatus = "None", CreatedAt = now });
            await db.SaveChangesAsync();
            db.DatGames.Add(new DatGameEntity { DatFileId = dat.Id, SourceEntryId = entry.Id, Name = name, CreatedAt = now });
        }
        await db.SaveChangesAsync();
        return (key, platform.Id);
    }
}
