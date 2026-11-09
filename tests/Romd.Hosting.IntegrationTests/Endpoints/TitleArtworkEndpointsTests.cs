using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Common.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     A title's artwork under <c>/api/titles/{titleId}</c>
///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"): each role is versioned by its own
///     revision, and the family root carries the validator built from every role revision that guards the saved
///     assets, rejections and hidden candidates.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TitleArtworkEndpointsTests(IntegrationTestFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Family_ReturnsEveryRoleWithTheFamilyRevisionAsTheETag_WithoutRevalidating()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();

        using var response = await client.GetAsync($"/api/titles/{titleId}/artwork");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var family = (await response.Content.ReadFromJsonAsync<TitleArtwork>(Json))!;
        response.Headers.ETag!.Tag.ShouldBe($"\"{family.Revision}\"");
        response.Headers.ETag.IsWeak.ShouldBeFalse();
        family.Roles.Select(role => role.Role).ShouldBe(Enum.GetNames<ArtworkRole>(), ignoreOrder: true);
        family.Roles.ShouldAllBe(role => role.Mode == ArtworkMode.Automatic && role.Revision == 0);
        // A publication changes a role without rotating its revision, so the validator never answers 304.
        using var conditional = Request(HttpMethod.Get, $"/api/titles/{titleId}/artwork", null);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag.Tag).ShouldBeTrue();
        (await client.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var collection in new[] { "artwork-assets", "artwork-rejections" })
        {
            using var member = await client.GetAsync($"/api/titles/{titleId}/{collection}");
            member.StatusCode.ShouldBe(HttpStatusCode.OK, collection);
            member.Headers.ETag!.Tag.ShouldBe(response.Headers.ETag.Tag, collection);
        }
    }

    [Fact]
    public async Task Role_ReturnsItsOwnRevisionAsTheETag_AndAnUnknownRoleOrTitleIs404()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();

        using var role = await client.GetAsync($"/api/titles/{titleId}/artwork/Backdrop");
        role.StatusCode.ShouldBe(HttpStatusCode.OK);
        var state = (await role.Content.ReadFromJsonAsync<ArtworkSelectionState>(Json))!;
        (state.Role, state.Revision).ShouldBe(("Backdrop", 0));
        role.Headers.ETag!.Tag.ShouldBe("\"0\"");

        foreach (var unknown in new[] { "backdrop", "Banner", "1" })
        {
            using var response = await client.GetAsync($"/api/titles/{titleId}/artwork/{unknown}");
            using (await ProblemEnvelope.ReadAsync(response, 404, "Artwork.UnknownRole"))
            { }
        }
        using var title = await client.GetAsync($"/api/titles/{IdCoder.Encode(int.MaxValue)}/artwork");
        using (await ProblemEnvelope.ReadAsync(title, 404, "Catalog.TitleNotFound"))
        { }
    }

    [Theory]
    [InlineData("GET", "artwork-candidates?providerId=igdb&gameId=1&role=Poster", null)]
    [InlineData("POST", "artwork-candidates/preview", null)]
    [InlineData("POST", "artwork-candidates/00000000000000000000000000000000/import", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("POST", "artwork/Poster/apply", "\"0\"")]
    [InlineData("PUT", "hidden-artwork-candidates/00000000000000000000000000000000", "\"0\"")]
    public async Task UnknownTitle_Is404BeforeAnyCandidateOrProviderWork(string method, string suffix, string? ifMatch)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var body = method == "GET" ? null : (object)new { requestId = Guid.NewGuid(), candidateReference = "unsigned" };

        using var response = await client.SendAsync(Request(new HttpMethod(method),
            $"/api/titles/{IdCoder.Encode(int.MaxValue)}/{suffix}", ifMatch, body));

        using (await ProblemEnvelope.ReadAsync(response, 404, "Catalog.TitleNotFound"))
        { }
    }

    [Theory]
    [InlineData("role=Banner", "role")]
    [InlineData("role=poster", "role")]
    [InlineData("role=1", "role")]
    [InlineData("role=99", "role")]
    [InlineData("role=Poster&mediaType=Box", "mediaType")]
    [InlineData("role=Poster&mediaType=cover", "mediaType")]
    [InlineData("role=Poster&mediaType=1", "mediaType")]
    public async Task BrowseCandidates_UnknownRoleOrMediaType_Is400KeyedByTheParameter(string query, string parameter)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();

        using var response = await client.GetAsync(
            $"/api/titles/{titleId}/artwork-candidates?providerId=steamgriddb&gameId=1&{query}");

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe([parameter]);
    }

    [Fact]
    public async Task ReplaceRole_RequiresTheRoleETag_AndAnswersTheRoleWithItsNewOne()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var path = $"/api/titles/{titleId}/artwork/Logo";
        var family = await FamilyETagAsync(client, titleId);
        var empty = new { mode = "Empty" };

        using (var missing = await client.SendAsync(Request(HttpMethod.Put, path, null, empty)))
        using (await ProblemEnvelope.ReadAsync(missing, 428, "Artwork.PreconditionRequired"))
        { }
        using (var stale = await client.SendAsync(Request(HttpMethod.Put, path, "\"7\"", empty)))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "Artwork.PreconditionFailed"))
        { }
        // A family ETag is not this role's validator.
        using (var family412 = await client.SendAsync(Request(HttpMethod.Put, path, family, empty)))
        using (await ProblemEnvelope.ReadAsync(family412, 412, "Artwork.PreconditionFailed"))
        { }
        (await FamilyETagAsync(client, titleId)).ShouldBe(family);

        using var left = await client.SendAsync(Request(HttpMethod.Put, path, "\"0\"", empty));
        left.StatusCode.ShouldBe(HttpStatusCode.OK, await left.Content.ReadAsStringAsync());
        var state = (await left.Content.ReadFromJsonAsync<ArtworkSelectionState>(Json))!;
        (state.Mode, state.Revision).ShouldBe((ArtworkMode.Empty, 1));
        left.Headers.ETag!.Tag.ShouldBe("\"1\"");
        // Every role write changes the family validator.
        (await FamilyETagAsync(client, titleId)).ShouldNotBe(family);

        using var automatic = await client.SendAsync(Request(HttpMethod.Put, path, left.Headers.ETag.Tag,
            new { mode = "Automatic" }));
        automatic.StatusCode.ShouldBe(HttpStatusCode.OK, await automatic.Content.ReadAsStringAsync());
        automatic.Headers.ETag!.Tag.ShouldBe("\"2\"");
        (await automatic.Content.ReadFromJsonAsync<ArtworkSelectionState>(Json))!.Mode.ShouldBe(ArtworkMode.Automatic);
    }

    [Fact]
    public async Task ReplaceRole_MediaPin_RequiresRoleETagAndReturnsItsCommittedAssetWithoutChangingMediaType()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var mediaId = await UploadPinMediaAsync(client, titleId);
        var path = $"/api/titles/{titleId}/artwork/Hero";
        var body = new { mode = "Pinned", mediaId, focalX = 0, focalY = 100 };

        using (var missing = await client.SendAsync(Request(HttpMethod.Put, path, null, body)))
        using (await ProblemEnvelope.ReadAsync(missing, 428, "Artwork.PreconditionRequired"))
        { }
        using (var stale = await client.SendAsync(Request(HttpMethod.Put, path, "\"7\"", body)))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "Artwork.PreconditionFailed"))
        { }
        using var pinned = await client.SendAsync(Request(HttpMethod.Put, path, "\"0\"", body));

        pinned.StatusCode.ShouldBe(HttpStatusCode.OK, await pinned.Content.ReadAsStringAsync());
        var state = (await pinned.Content.ReadFromJsonAsync<ArtworkSelectionState>(Json))!;
        (state.Role, state.Mode, state.Revision).ShouldBe(("Hero", ArtworkMode.Pinned, 1));
        state.PinnedAssetId.ShouldNotBeNull();
        pinned.Headers.ETag!.Tag.ShouldBe("\"1\"");
        using var current = await client.GetAsync(path);
        current.Headers.ETag!.Tag.ShouldBe(pinned.Headers.ETag.Tag);
        (await current.Content.ReadFromJsonAsync<ArtworkSelectionState>(Json)).ShouldBe(state);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var id = Sqid.Parse(titleId, null).Value;
        var selection = await db.ArtworkSelections.SingleAsync(row => row.TitleId == id && row.Role == ArtworkRole.Hero);
        (selection.FocalX, selection.FocalY).ShouldBe((0, 100));
        var media = await db.TitleMedia.SingleAsync(row => row.Id == Sqid.Parse(mediaId, null).Value);
        media.Type.ShouldBe("Cover");
        var asset = await db.ArtworkAssets.Include(row => row.Variants).SingleAsync(row => row.Id == selection.PinnedAssetId);
        asset.OriginalFileId.ShouldBe(media.FileId);
        asset.Variants.ShouldNotBeEmpty();
        using var replay = await client.SendAsync(Request(HttpMethod.Put, path, "\"0\"", body));
        using (await ProblemEnvelope.ReadAsync(replay, 412, "Artwork.PreconditionFailed"))
        { }
        (await db.ArtworkAssets.CountAsync(row => row.TitleId == id)).ShouldBe(1);
    }

    [Theory]
    [InlineData(-1, 50, "focalX")]
    [InlineData(50, 101, "focalY")]
    public async Task ReplaceRole_MediaPinInvalidFocal_IsFieldValidationBeforeMutation(int x, int y, string field)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var mediaId = await UploadPinMediaAsync(client, titleId);
        var path = $"/api/titles/{titleId}/artwork/Hero";

        using var response = await client.SendAsync(Request(HttpMethod.Put, path, "\"0\"",
            new { mode = "Pinned", mediaId, focalX = x, focalY = y }));

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Artwork.InvalidSelection");
        problem.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        (await client.GetFromJsonAsync<ArtworkSelectionState>(path, Json))!.Revision.ShouldBe(0);
    }

    [Theory]
    [InlineData(RomdRoleType.User)]
    [InlineData(RomdRoleType.Contributor)]
    public async Task ReplaceRole_MediaPinBelowManager_IsForbidden(RomdRoleType role)
    {
        var titleId = await SeedTitleAsync();
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [role]);

        using var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/titles/{titleId}/artwork/Hero", "\"0\"",
            new { mode = "Pinned", mediaId = IdCoder.Encode(1) }));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("""{"mode":"Pinned"}""", "assetId")]
    [InlineData("""{"mode":"Pinned","assetId":"AAAA","mediaId":"AAAA"}""", "assetId")]
    [InlineData("""{"mode":"Pinned","assetId":"not-an-id"}""", "assetId")]
    [InlineData("""{"mode":"Pinned","mediaId":""}""", "mediaId")]
    [InlineData("""{"mode":"Empty","assetId":"AAAA"}""", "assetId")]
    [InlineData("""{"mode":"Automatic","focalX":50}""", "focalX")]
    public async Task ReplaceRole_InvalidSelection_Is400KeyedByItsField_AndWritesNothing(string body, string field)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var path = $"/api/titles/{titleId}/artwork/Poster";
        var valid = IdCoder.Encode(1);
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(body.Replace("AAAA", valid), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"").ShouldBeTrue();

        using var response = await client.SendAsync(request);

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Artwork.InvalidSelection");
        problem.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        (await client.GetFromJsonAsync<ArtworkSelectionState>(path, Json))!.Revision.ShouldBe(0);
    }

    [Theory]
    [InlineData("""{"mode":"Hidden"}""")]
    [InlineData("""{"mode":2}""")]
    [InlineData("""{}""")]
    public async Task ReplaceRole_UnknownOrMissingMode_Is400(string body)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/titles/{titleId}/artwork/Poster")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"").ShouldBeTrue();

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("PUT", "provider-matches/igdb", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("POST", "artwork/Poster/apply", "\"0\"")]
    [InlineData("POST", "artwork-candidates/00000000000000000000000000000000/import", null)]
    [InlineData("PUT", "hidden-artwork-candidates/00000000000000000000000000000000", "family")]
    [InlineData("POST", "artwork-candidates/preview", null)]
    public async Task BodyWrite_MissingRequiredMember_Is400AndWritesNothing(string method, string suffix, string? ifMatch)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var revision = await StoredRevisionAsync(titleId);
        var path = suffix.StartsWith('/') ? suffix : $"/api/titles/{titleId}/{suffix}";
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        // An import is a title write and a hide a family write; send their own ETags so only the body can fail.
        var tag = ifMatch switch
        {
            null => $"\"{revision}\"",
            "family" => await FamilyETagAsync(client, titleId),
            _ => ifMatch
        };
        request.Headers.TryAddWithoutValidation("If-Match", tag).ShouldBeTrue();

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await StoredRevisionAsync(titleId)).ShouldBe(revision);
        (await client.GetFromJsonAsync<TitleArtwork>($"/api/titles/{titleId}/artwork", Json))!.Roles
            .ShouldAllBe(role => role.Revision == 0);
    }

    [Fact]
    public async Task FamilyWrites_RequireTheFamilyETag_AndAnUnknownAssetIs404()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var family = await FamilyETagAsync(client, titleId);
        var unknown = IdCoder.Encode(int.MaxValue);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Delete, $"/api/titles/{titleId}/artwork-assets/{unknown}"),
                     (HttpMethod.Put, $"/api/titles/{titleId}/artwork-rejections/{unknown}"),
                     (HttpMethod.Delete, $"/api/titles/{titleId}/artwork-rejections/{unknown}")
                 })
        {
            using (var missing = await client.SendAsync(Request(method, path, null)))
            using (await ProblemEnvelope.ReadAsync(missing, 428, "Artwork.PreconditionRequired"))
            { }
            // A role's validator is not the family's.
            using (var stale = await client.SendAsync(Request(method, path, "\"0\"")))
            using (await ProblemEnvelope.ReadAsync(stale, 412, "Artwork.PreconditionFailed"))
            { }
            using (var absent = await client.SendAsync(Request(method, path, family)))
            using (await ProblemEnvelope.ReadAsync(absent, 404, "Artwork.AssetNotFound"))
            { }
        }

        (await FamilyETagAsync(client, titleId)).ShouldBe(family);
    }

    [Fact]
    public async Task DeleteAsset_AnswersTheFamilyWithItsNewETag()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var assetId = await SeedAssetAsync(titleId);
        var family = await FamilyETagAsync(client, titleId);
        var assets = (await client.GetFromJsonAsync<Page<SavedArtwork>>($"/api/titles/{titleId}/artwork-assets"))!;
        assets.Items.ShouldHaveSingleItem().Id.ShouldBe(assetId);

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/titles/{titleId}/artwork-assets/{assetId}", family));

        deleted.StatusCode.ShouldBe(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        var after = (await deleted.Content.ReadFromJsonAsync<TitleArtwork>(Json))!;
        deleted.Headers.ETag!.Tag.ShouldBe($"\"{after.Revision}\"");
        deleted.Headers.ETag.Tag.ShouldNotBe(family);
        (await FamilyETagAsync(client, titleId)).ShouldBe(deleted.Headers.ETag.Tag);
        (await client.GetFromJsonAsync<Page<SavedArtwork>>($"/api/titles/{titleId}/artwork-assets"))!.Items.ShouldBeEmpty();
        // The image is gone; deleting it again names nothing.
        using var again = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/titles/{titleId}/artwork-assets/{assetId}", deleted.Headers.ETag.Tag));
        using (await ProblemEnvelope.ReadAsync(again, 404, "Artwork.AssetNotFound"))
        { }
    }

    [Fact]
    public async Task RejectAndRestore_ChainTheFamilyETag()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var assetId = await SeedAssetAsync(titleId);
        var path = $"/api/titles/{titleId}/artwork-rejections/{assetId}";

        using var rejected = await client.SendAsync(Request(HttpMethod.Put, path, await FamilyETagAsync(client, titleId)));
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, await rejected.Content.ReadAsStringAsync());
        var family = (await rejected.Content.ReadFromJsonAsync<TitleArtwork>(Json))!;
        family.Roles.Single(role => role.Role == "Poster").Revision.ShouldBe(1);
        (await client.GetFromJsonAsync<Page<RejectedArtwork>>($"/api/titles/{titleId}/artwork-rejections"))!
            .Items.ShouldHaveSingleItem().Id.ShouldBe(assetId);

        using var restored = await client.SendAsync(Request(HttpMethod.Delete, path, rejected.Headers.ETag!.Tag));
        restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());
        restored.Headers.ETag!.Tag.ShouldNotBe(rejected.Headers.ETag.Tag);
        (await client.GetFromJsonAsync<Page<RejectedArtwork>>($"/api/titles/{titleId}/artwork-rejections"))!.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("igdb")]
    [InlineData("gallery:igdb")]
    public async Task DeleteTitleMedia_LeavesTheArtworkToo_SoItsImageIsNeverAcquiredAgain(string sourceId)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var assetId = await SeedAssetAsync(titleId);
        var url = $"https://images.igdb.com/{Guid.NewGuid():N}.png";
        int mediaId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var asset = await db.ArtworkAssets.SingleAsync(item => item.Id == Sqid.Parse(assetId, null).Value);
            var media = new TitleMediaEntity
            {
                TitleId = asset.TitleId,
                FileId = asset.OriginalFileId,
                Type = "Cover",
                SourceId = sourceId,
                SourceUrl = url,
                ContentType = "image/png",
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.TitleMedia.Add(media);
            await db.SaveChangesAsync();
            mediaId = media.Id;
        }
        using (var pin = await client.SendAsync(Request(HttpMethod.Put, $"/api/titles/{titleId}/artwork/Poster", "\"0\"",
                   new { mode = "Pinned", assetId })))
            pin.StatusCode.ShouldBe(HttpStatusCode.OK, await pin.Content.ReadAsStringAsync());
        var family = await FamilyETagAsync(client, titleId);
        using var title = await client.GetAsync($"/api/titles/{titleId}");

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/titles/{titleId}/media/{IdCoder.Encode(mediaId)}", title.Headers.ETag!.Tag));

        deleted.StatusCode.ShouldBe(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        using (var scope = fixture.Services.CreateScope())
        {
            var curation = scope.ServiceProvider.GetRequiredService<Romd.Admin.Application.Artwork.IArtworkCurationRepository>();
            var id = Sqid.Parse(titleId, null).Value;
            (await curation.GetExcludedUrlsAsync(id, default)).ShouldContain(url);
            (await curation.GetGalleryAsync(id, default)).Value.ShouldBeEmpty();
        }
        (await client.GetFromJsonAsync<ArtworkSelectionState>($"/api/titles/{titleId}/artwork/Poster", Json))!.Mode
            .ShouldBe(ArtworkMode.Automatic);
        (await FamilyETagAsync(client, titleId)).ShouldNotBe(family);
    }

    [Theory]
    [InlineData("igdb")]
    [InlineData("gallery:igdb")]
    public async Task DeleteTitleMedia_MediaOnlyImage_ExcludesItsUrlSoItIsNeverAcquiredAgain(string sourceId)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var url = $"https://images.igdb.com/{Guid.NewGuid():N}.png";
        int mediaId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var file = new FileEntityPersistence
            {
                Sha256 = Romd.Domain.Hashing.Sha256.FromBytes(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()),
                Size = 10,
                SizeOnDisk = 10,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Files.Add(file);
            await db.SaveChangesAsync();
            var media = new TitleMediaEntity
            {
                TitleId = Sqid.Parse(titleId, null).Value,
                FileId = file.Id,
                Type = "Screenshot",
                SourceId = sourceId,
                SourceUrl = url,
                ContentType = "image/png",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.TitleMedia.Add(media);
            await db.SaveChangesAsync();
            mediaId = media.Id;
        }
        using var title = await client.GetAsync($"/api/titles/{titleId}");

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/titles/{titleId}/media/{IdCoder.Encode(mediaId)}", title.Headers.ETag!.Tag));

        deleted.StatusCode.ShouldBe(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        using (var scope = fixture.Services.CreateScope())
        {
            var curation = scope.ServiceProvider.GetRequiredService<Romd.Admin.Application.Artwork.IArtworkCurationRepository>();
            (await curation.GetExcludedUrlsAsync(Sqid.Parse(titleId, null).Value, default)).ShouldContain(url);
        }
    }

    [Fact]
    public async Task ArtworkAcquisition_LivesUnderTheTitle_AndFillQueuesWithoutAPrecondition()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();

        (await client.PostAsync($"/api/titles/{titleId}/artwork-acquisition/fill", null)).StatusCode
            .ShouldBe(HttpStatusCode.Accepted);
        var state = (await client.GetFromJsonAsync<ArtworkAcquisitionState>($"/api/titles/{titleId}/artwork-acquisition"))!;
        state.ActiveJobId.ShouldNotBeNull();
        using var missing = await client.GetAsync($"/api/titles/{IdCoder.Encode(int.MaxValue)}/artwork-acquisition");
        using (await ProblemEnvelope.ReadAsync(missing, 404, "Catalog.TitleNotFound"))
        { }
    }

    [Theory]
    [InlineData("GET", "/api/artwork/titles/{titleId}/selections")]
    [InlineData("GET", "/api/artwork/titles/{titleId}/saved")]
    [InlineData("GET", "/api/artwork/titles/{titleId}/rejected")]
    [InlineData("GET", "/api/artwork/titles/{titleId}/games")]
    [InlineData("GET", "/api/artwork/titles/{titleId}/candidates")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/preview")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/gallery")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/apply")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/delete")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/hide")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/roles/Poster/pin")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/roles/Poster/empty")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/roles/Poster/automatic")]
    [InlineData("POST", "/api/artwork/titles/{titleId}/roles/Poster/reject")]
    [InlineData("GET", "/api/enrichment/artwork/{titleId}")]
    [InlineData("POST", "/api/enrichment/artwork/{titleId}")]
    [InlineData("POST", "/api/export/title/{titleId}")]
    [InlineData("POST", "/api/export/title/{titleId}/download-ticket")]
    [InlineData("POST", "/api/export/titles/{titleId}/releases/{titleId}/download-ticket")]
    [InlineData("POST", "/api/export/files/{titleId}/download-ticket")]
    [InlineData("POST", "/api/titles/{titleId}/artwork-candidates/import")]
    [InlineData("GET", "/api/titles/{titleId}/artwork-games")]
    [InlineData("GET", "/api/artwork-providers/steamgriddb/games?query=Metroid")]
    [InlineData("POST", "/api/titles/{titleId}/export")]
    [InlineData("DELETE", "/api/titles/{titleId}/hidden-artwork-candidates/00000000000000000000000000000000")]
    [InlineData("POST", "/api/titles/{titleId}/download-tickets")]
    [InlineData("POST", "/api/titles/{titleId}/releases/{titleId}/download-tickets")]
    [InlineData("POST", "/api/roms/{titleId}/download-tickets")]
    [InlineData("GET", "/api/export/download/0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0")]
    public async Task RetiredRoutes_AnswerOnlyTheRoutingProblem(string method, string template)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method),
            template.Replace("{titleId}", IdCoder.Encode(1)))
        {
            Content = method == "GET" ? null : JsonContent.Create(new { })
        });

        ((int)response.StatusCode).ShouldBeOneOf(404, 405);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("errorCode").GetString()
            .ShouldBeOneOf("Request.RouteNotFound", "Request.MethodNotAllowed");
    }

    private static async Task<string> FamilyETagAsync(HttpClient client, string titleId)
    {
        using var response = await client.GetAsync($"/api/titles/{titleId}/artwork");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? ifMatch, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    private async Task<Guid> StoredRevisionAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Titles
            .Where(title => title.Id == id).Select(title => title.Revision).SingleAsync();
    }

    private async Task<string> UploadPinMediaAsync(HttpClient client, string titleId)
    {
        using var bitmap = new SKBitmap(160, 90);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/titles/{titleId}/media?type=cover")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(encoded.ToArray()), "file", "pin.png" } }
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{await StoredRevisionAsync(titleId)}\"").ShouldBeTrue();
        using var uploaded = await client.SendAsync(request);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        return (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> SeedTitleAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Artwork Test Title {Guid.NewGuid():N}";
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
        return IdCoder.Encode(title.Id);
    }

    /// <summary>A saved Poster asset whose original and variant are ledger files of their own.</summary>
    private async Task<string> SeedAssetAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var files = Enumerable.Range(0, 2).Select(_ => new FileEntityPersistence
        {
            Sha256 = Romd.Domain.Hashing.Sha256.FromBytes(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()),
            Size = 10,
            SizeOnDisk = 10,
            CreatedAt = DateTimeOffset.UtcNow
        }).ToArray();
        db.Files.AddRange(files);
        await db.SaveChangesAsync();
        var asset = new ArtworkAssetEntity
        {
            TitleId = Sqid.Parse(titleId, null).Value,
            Role = ArtworkRole.Poster,
            SourceId = "user",
            OriginalFileId = files[0].Id,
            ContentVersion = files[0].Sha256.ToString(),
            ContentType = "image/png",
            Width = 600,
            Height = 900,
            IsEligible = true,
            CreatedAt = DateTimeOffset.UtcNow,
            Variants =
            [
                new ArtworkVariantEntity
                {
                    Name = "card", FileId = files[1].Id, ContentVersion = files[1].Sha256.ToString(),
                    ContentType = "image/webp", Width = 300, Height = 450
                }
            ]
        };
        db.ArtworkAssets.Add(asset);
        await db.SaveChangesAsync();
        return IdCoder.Encode(asset.Id);
    }
}
