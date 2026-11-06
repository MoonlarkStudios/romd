using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class S6ArtworkResourceContractTests(IntegrationTestFixture fixture)
{
    private const string SteamGridSettings = "/api/artwork-providers/steamgriddb/settings";
    private const string AcquisitionSettings = "/api/enrichment/artwork/settings";

    [Fact]
    public async Task ArtworkFixture_ExistingGalleryProjection_PreservesEligibilityAndRejectedRows()
    {
        var seeded = await SeedArtworkAsync();
        using var scope = fixture.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IArtworkCurationRepository>();
        var titleId = Sqid.Parse(seeded.TitleId, null).Value;
        var gallery = await repository.GetGalleryAsync(titleId, CancellationToken.None);
        gallery.IsError.ShouldBeFalse();
        gallery.Value.Select(asset => IdCoder.Encode(asset.Id)).ShouldBe(seeded.SavedIds);
        gallery.Value.ShouldAllBe(asset => asset.IsEligible && asset.Variants.Count > 0);
        var rejected = await repository.GetRejectedAsync(titleId, CancellationToken.None);
        rejected.IsError.ShouldBeFalse();
        rejected.Value.Select(asset => IdCoder.Encode(asset.Id)).ShouldBe(seeded.RejectedIds);
        rejected.Value.ShouldContain(asset => !asset.IsEligible);
    }

    [Fact]
    public async Task Providers_CanonicalCollection_ContainsConfiguredRegistryAndFiltersRole()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/artwork-providers");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        body.RootElement.EnumerateArray().Select(provider => provider.GetProperty("id").GetString())
            .ShouldContain("steamgriddb");
        body.RootElement.EnumerateArray().Select(provider => provider.GetProperty("id").GetString())
            .ShouldContain("igdb");

        using var filtered = await client.GetAsync("/api/artwork-providers?role=Hero");
        filtered.StatusCode.ShouldBe(HttpStatusCode.OK, await filtered.Content.ReadAsStringAsync());
        using var filteredBody = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        filteredBody.RootElement.GetArrayLength().ShouldBeGreaterThan(0);
        foreach (var provider in filteredBody.RootElement.EnumerateArray())
            provider.GetProperty("roles").EnumerateArray().Select(role => role.GetProperty("role").GetString())
                .ShouldBe(["Hero"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("hero")]
    [InlineData("1")]
    public async Task Providers_InvalidRoleWithoutTitle_ReturnsAttributedValidation(string role)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/artwork-providers?role={Uri.EscapeDataString(role)}");
        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").TryGetProperty("role", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/artwork/providers")]
    [InlineData("/api/admin/artwork-providers/steamgriddb")]
    public async Task ArtworkResources_RetiredRoute_ReturnsRoutingProblem(string path)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var body = await ProblemEnvelope.ReadAsync(response);
        body.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Assets_EqualCreationTimes_PageWithoutDuplicatesAndCarryFamilyValidator()
    {
        var seeded = await SeedArtworkAsync();
        using var client = fixture.CreateAuthenticatedClient();
        var path = $"/api/titles/{seeded.TitleId}/artwork-assets";
        using var family = await client.GetAsync($"/api/titles/{seeded.TitleId}/artwork");
        family.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tag = family.Headers.ETag.ShouldNotBeNull().Tag;
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            using var response = await client.GetAsync(path + "?limit=1" + CursorQuery(cursor));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            response.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(tag);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = AssertPage(body);
            root.GetProperty("items").GetArrayLength().ShouldBe(1);
            ids.Add(root.GetProperty("items")[0].GetProperty("id").GetString()!);
            cursor = root.GetProperty("nextCursor").GetString();
            root.GetProperty("hasNextPage").GetBoolean().ShouldBe(cursor is not null);
            ids.Count.ShouldBeLessThanOrEqualTo(seeded.SavedIds.Length);
        } while (cursor is not null);
        ids.ShouldBe(seeded.SavedIds);
        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public async Task Rejections_IneligibleRowsRemainVisibleAndPageInIdOrder()
    {
        var seeded = await SeedArtworkAsync();
        using var client = fixture.CreateAuthenticatedClient();
        var path = $"/api/titles/{seeded.TitleId}/artwork-rejections";
        var ids = new List<string>();
        string? tag = null;
        string? cursor = null;
        do
        {
            using var response = await client.GetAsync(path + "?limit=1" + CursorQuery(cursor));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            tag ??= response.Headers.ETag.ShouldNotBeNull().Tag;
            response.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(tag);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = AssertPage(body);
            ids.Add(root.GetProperty("items")[0].GetProperty("id").GetString()!);
            cursor = root.GetProperty("nextCursor").GetString();
            ids.Count.ShouldBeLessThanOrEqualTo(seeded.RejectedIds.Length);
        } while (cursor is not null);
        ids.ShouldBe(seeded.RejectedIds);
    }

    [Fact]
    public async Task Assets_ContinuationSurvivesFamilyChangeAndLimitChangeWhileOldWriteValidatorFails()
    {
        var seeded = await SeedArtworkAsync();
        using var client = fixture.CreateAuthenticatedClient();
        var title = $"/api/titles/{seeded.TitleId}";
        using var first = await client.GetAsync(title + "/artwork-assets?limit=1");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var oldFamily = first.Headers.ETag.ShouldNotBeNull().Tag;
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = AssertPage(firstBody).GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();
        using var role = await client.GetAsync(title + "/artwork/Poster");
        using var change = new HttpRequestMessage(HttpMethod.Put, title + "/artwork/Poster")
        {
            Content = JsonContent.Create(new { mode = "Empty" })
        };
        change.Headers.TryAddWithoutValidation("If-Match", role.Headers.ETag.ShouldNotBeNull().Tag).ShouldBeTrue();
        using var changed = await client.SendAsync(change);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        using var next = await client.GetAsync(title + "/artwork-assets?limit=100" + CursorQuery(cursor));
        next.StatusCode.ShouldBe(HttpStatusCode.OK, await next.Content.ReadAsStringAsync());
        next.Headers.ETag.ShouldNotBeNull().Tag.ShouldNotBe(oldFamily);
        using var nextBody = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        var page = AssertPage(nextBody);
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
            .ShouldBe(seeded.SavedIds.Skip(1));
        page.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        using var stale = new HttpRequestMessage(HttpMethod.Delete, title + "/artwork-assets/" + seeded.SavedIds[0]);
        stale.Headers.TryAddWithoutValidation("If-Match", oldFamily).ShouldBeTrue();
        using var rejected = await client.SendAsync(stale);
        using var problem = await ProblemEnvelope.ReadAsync(rejected, 412, "Artwork.PreconditionFailed");
    }

    [Theory]
    [InlineData("artwork-assets", "limit=0")]
    [InlineData("artwork-assets", "limit=101")]
    [InlineData("artwork-assets", "cursor=")]
    [InlineData("artwork-assets", "cursor=!")]
    [InlineData("artwork-assets", "cursor=AAAA=")]
    [InlineData("artwork-rejections", "limit=0")]
    [InlineData("artwork-rejections", "limit=101")]
    [InlineData("artwork-rejections", "cursor=")]
    [InlineData("artwork-rejections", "cursor=!")]
    [InlineData("artwork-rejections", "cursor=AAAA=")]
    public async Task ArtworkLists_InvalidPaging_ReturnsProblem(string resource, string query)
    {
        var seeded = await SeedArtworkAsync();
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/titles/{seeded.TitleId}/{resource}?{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ProblemEnvelope.ReadAsync(response);
        body.RootElement.GetProperty("errors").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task ArtworkLists_CursorCannotCrossTitleOrResourceOrExceedBudget()
    {
        var first = await SeedArtworkAsync();
        var second = await SeedArtworkAsync();
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/titles/{first.TitleId}/artwork-assets?limit=1");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cursor = AssertPage(body).GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();
        foreach (var path in new[]
                 {
                     $"/api/titles/{second.TitleId}/artwork-assets?limit=1" + CursorQuery(cursor),
                     $"/api/titles/{first.TitleId}/artwork-rejections?limit=1" + CursorQuery(cursor),
                     $"/api/titles/{first.TitleId}/artwork-assets?cursor={new string('a', 1025)}"
                 })
        {
            using var rejected = await client.GetAsync(path);
            rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest, path);
            using var problem = await ProblemEnvelope.ReadAsync(rejected);
        }
    }

    [Theory]
    [InlineData("artwork-assets")]
    [InlineData("artwork-rejections")]
    public async Task ArtworkLists_EmptyTitle_ReturnsEmptyPageWithFamilyETagAndNever304(string resource)
    {
        var titleId = await SeedTitleAsync();
        using var client = fixture.CreateAuthenticatedClient();
        using var family = await client.GetAsync($"/api/titles/{titleId}/artwork");
        var tag = family.Headers.ETag.ShouldNotBeNull().Tag;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/titles/{titleId}/{resource}");
        request.Headers.TryAddWithoutValidation("If-None-Match", tag).ShouldBeTrue();
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(tag);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = AssertPage(body);
        root.GetProperty("items").GetArrayLength().ShouldBe(0);
        root.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        root.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("artwork-assets")]
    [InlineData("artwork-rejections")]
    [InlineData("artwork-candidates?providerId=missing&gameId=missing&role=Hero")]
    public async Task ArtworkResources_UnknownTitle_ReturnsTitleNotFound(string resource)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/titles/{IdCoder.Encode(int.MaxValue)}/{resource}");
        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Catalog.TitleNotFound");
    }

    [Theory]
    [InlineData(SteamGridSettings)]
    [InlineData(AcquisitionSettings)]
    public async Task Settings_Get_UsesRevisionETagWithoutConditional304(string path)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var first = await client.GetAsync(path);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var tag = first.Headers.ETag.ShouldNotBeNull().Tag;
        tag.ShouldBe($"\"{body.RootElement.GetProperty("revision").GetString()}\"");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("If-None-Match", tag).ShouldBeTrue();
        using var repeated = await client.SendAsync(request);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeated.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(tag);
    }

    [Theory]
    [InlineData(SteamGridSettings, null, 428)]
    [InlineData(SteamGridSettings, "*", 428)]
    [InlineData(SteamGridSettings, "W/\"stale\"", 412)]
    [InlineData(SteamGridSettings, "malformed", 400)]
    [InlineData(SteamGridSettings, "\"11111111-1111-1111-1111-111111111111\"", 412)]
    [InlineData(AcquisitionSettings, null, 428)]
    [InlineData(AcquisitionSettings, "*", 428)]
    [InlineData(AcquisitionSettings, "W/\"stale\"", 412)]
    [InlineData(AcquisitionSettings, "malformed", 400)]
    [InlineData(AcquisitionSettings, "\"11111111-1111-1111-1111-111111111111\"", 412)]
    public async Task Settings_InvalidPrecondition_DoesNotWrite(string path, string? ifMatch, int status)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var before = await client.GetAsync(path);
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        var original = await before.Content.ReadAsStringAsync();
        using var request = WriteSettings(path, ifMatch);
        using var response = await client.SendAsync(request);
        ((int)response.StatusCode).ShouldBe(status, await response.Content.ReadAsStringAsync());
        using var body = await ProblemEnvelope.ReadAsync(response);
        if (status == 400)
        {
            body.RootElement.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidParameter");
            body.RootElement.GetProperty("errors").TryGetProperty("If-Match", out _).ShouldBeTrue();
        }
        else
            body.RootElement.GetProperty("errorCode").GetString().ShouldEndWith(status == 428 ? ".PreconditionRequired" : ".PreconditionFailed");
        using var after = await client.GetAsync(path);
        (await after.Content.ReadAsStringAsync()).ShouldBe(original);
        after.Headers.ETag?.Tag.ShouldBe(before.Headers.ETag?.Tag);
    }

    [Theory]
    [InlineData(SteamGridSettings)]
    [InlineData(AcquisitionSettings)]
    public async Task Settings_HeaderBasedReplacement_ReturnsOwnRevisionAndRejectsStaleWrite(string path)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var initial = await client.GetAsync(path);
        initial.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tag = initial.Headers.ETag.ShouldNotBeNull().Tag;
        using var request = WriteSettings(path, tag);
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var acceptedTag = response.Headers.ETag.ShouldNotBeNull().Tag;
        acceptedTag.ShouldNotBe(tag);
        acceptedTag.ShouldBe($"\"{body.RootElement.GetProperty("revision").GetString()}\"");
        using var stale = WriteSettings(path, tag);
        using var rejected = await client.SendAsync(stale);
        rejected.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var current = await client.GetAsync(path);
        current.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(acceptedTag);
    }

    [Fact]
    public async Task SteamGridConnectionTest_LocalFailure_RotatesStatusRevisionAndRejectsOldPrecondition()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var initial = await client.GetAsync(SteamGridSettings);
        initial.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var reset = WriteSettings(SteamGridSettings, initial.Headers.ETag.ShouldNotBeNull().Tag,
            new { enabled = false, apiKey = (string?)null, clearApiKey = true });
        using var saved = await client.SendAsync(reset);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var tag = saved.Headers.ETag.ShouldNotBeNull().Tag;
        using var request = new HttpRequestMessage(HttpMethod.Post, SteamGridSettings + "/test-connection");
        request.Headers.TryAddWithoutValidation("If-Match", tag).ShouldBeTrue();
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("lastTestSucceeded").GetBoolean().ShouldBeFalse();
        body.RootElement.GetProperty("lastTestedAt").ValueKind.ShouldBe(JsonValueKind.String);
        var testedTag = response.Headers.ETag.ShouldNotBeNull().Tag;
        testedTag.ShouldNotBe(tag);
        testedTag.ShouldBe($"\"{body.RootElement.GetProperty("revision").GetString()}\"");
        using var stale = new HttpRequestMessage(HttpMethod.Post, SteamGridSettings + "/test-connection");
        stale.Headers.TryAddWithoutValidation("If-Match", tag).ShouldBeTrue();
        using var rejected = await client.SendAsync(stale);
        rejected.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Workbench_PageHasNoCountsAndCursorBindsQueryAndView()
    {
        var prefix = $"S6 Workbench {Guid.NewGuid():N}";
        var firstId = await SeedTitleAsync(prefix + " One", tracked: true);
        _ = await SeedTitleAsync(prefix + " Two", tracked: true);
        using var client = fixture.CreateAuthenticatedClient();
        var path = $"/api/enrichment/workbench?view=All&limit=1&query={Uri.EscapeDataString(prefix)}";
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = AssertPage(body);
        root.TryGetProperty("counts", out _).ShouldBeFalse();
        root.GetProperty("items")[0].GetProperty("titleId").GetString().ShouldBe(firstId);
        var cursor = root.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();
        foreach (var changed in new[]
                 {
                     $"/api/enrichment/workbench?view=NeedsReview&limit=1&query={Uri.EscapeDataString(prefix)}" + CursorQuery(cursor),
                     "/api/enrichment/workbench?view=All&limit=1&query=other" + CursorQuery(cursor)
                 })
        {
            using var rejected = await client.GetAsync(changed);
            rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using var problem = await ProblemEnvelope.ReadAsync(rejected);
        }
        using var stats = await client.GetAsync($"/api/enrichment/workbench/stats?query={Uri.EscapeDataString(prefix)}");
        stats.StatusCode.ShouldBe(HttpStatusCode.OK, await stats.Content.ReadAsStringAsync());
        using var statsBody = JsonDocument.Parse(await stats.Content.ReadAsStringAsync());
        statsBody.RootElement.GetProperty("total").GetInt32().ShouldBe(2);
    }

    private static JsonElement AssertPage(JsonDocument body)
    {
        var root = body.RootElement;
        root.ValueKind.ShouldBe(JsonValueKind.Object);
        root.EnumerateObject().Select(property => property.Name).Order().ShouldBe(["hasNextPage", "items", "nextCursor"]);
        root.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);
        return root;
    }

    private static string CursorQuery(string? cursor) => cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor);

    private static HttpRequestMessage WriteSettings(string path, string? ifMatch, object? body = null)
    {
        body ??= path == SteamGridSettings
            ? new { enabled = false, apiKey = (string?)null, clearApiKey = false }
            : (object)new { fillPosters = true, fillHeroes = true, fillLogos = true, fillBackdrops = true, reviewBackdrops = false };
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    private async Task<string> SeedTitleAsync(string? name = null, bool tracked = false)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        name ??= $"S6 Artwork {Guid.NewGuid():N}";
        var title = new TitleEntity
        {
            PlatformId = await db.Platforms.OrderBy(platform => platform.Id).Select(platform => platform.Id).FirstAsync(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        if (tracked)
        {
            db.TrackedTitles.Add(new() { TitleId = title.Id });
            await db.SaveChangesAsync();
        }
        return IdCoder.Encode(title.Id);
    }

    private async Task<SeededArtwork> SeedArtworkAsync()
    {
        var titleId = await SeedTitleAsync();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var files = Enumerable.Range(0, 12).Select(_ => new FileEntityPersistence
        {
            Sha256 = Sha256.FromBytes(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()),
            Size = 10,
            SizeOnDisk = 10,
            CreatedAt = time
        }).ToArray();
        db.Files.AddRange(files);
        await db.SaveChangesAsync();
        var assets = files.Take(6).Select((file, index) => new ArtworkAssetEntity
        {
            TitleId = Sqid.Parse(titleId, null).Value,
            Role = ArtworkRole.Poster,
            SourceId = "user",
            OriginalFileId = file.Id,
            ContentVersion = file.Sha256.ToString(),
            ContentType = "image/png",
            Width = 600,
            Height = 900,
            CreatedAt = time,
            IsEligible = index < 4,
            IsRejected = index is 3 or 4,
            ProviderAssetId = $"s6-{index}",
            Variants =
            [
                new ArtworkVariantEntity
                {
                    Name = "card", FileId = files[index + 6].Id, ContentVersion = files[index + 6].Sha256.ToString(),
                    ContentType = "image/webp", Width = 300, Height = 450
                }
            ]
        }).ToArray();
        db.ArtworkAssets.AddRange(assets);
        await db.SaveChangesAsync();
        return new(titleId, assets.Where(asset => asset.IsEligible && !asset.IsRejected).OrderBy(asset => asset.Id)
            .Select(asset => IdCoder.Encode(asset.Id)).ToArray(), assets.Where(asset => asset.IsRejected).OrderBy(asset => asset.Id)
            .Select(asset => IdCoder.Encode(asset.Id)).ToArray());
    }

    private sealed record SeededArtwork(string TitleId, string[] SavedIds, string[] RejectedIds);
}
