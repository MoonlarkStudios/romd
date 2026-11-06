using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     The core title family under <c>/api/titles/{titleId}</c> (docs/decisions/api-contract-policy.md,
///     "Idempotency And Concurrency"): one representation carries <c>Titles.Revision</c> as its strong ETag, every
///     family write requires it in <c>If-Match</c> and returns the next one, and tracking and game moves stay
///     unconditional.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TitleConcurrencyEndpointsTests(IntegrationTestFixture fixture)
{
    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00];

    [Fact]
    public async Task Get_ReturnsTheDetailWithItsRevisionAsTheStrongETag_WithoutRevalidating()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var revision = await StoredRevisionAsync(titleId);

        using var response = await client.GetAsync($"/api/titles/{titleId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            body.RootElement.GetProperty("id").GetString().ShouldBe(titleId);
            body.RootElement.GetProperty("revision").GetString().ShouldBe(revision.ToString());
            body.RootElement.TryGetProperty("releases", out _).ShouldBeTrue();
        }
        response.Headers.ETag.ShouldNotBeNull().IsWeak.ShouldBeFalse();
        response.Headers.ETag.Tag.ShouldBe($"\"{revision}\"");

        // Titles.Revision does not version artwork, tracking or release facts, so the GET never answers 304.
        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/titles/{titleId}");
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag);
        (await client.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_UnknownTitle_Is404Envelope()
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.GetAsync($"/api/titles/{IdCoder.Encode(int.MaxValue)}");

        using (await ProblemEnvelope.ReadAsync(response, 404, "Catalog.TitleNotFound"))
        { }
    }

    [Fact]
    public async Task FamilyMembers_ReturnTheTitleValidator_AndNeverRevalidate()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        using var uploaded = await client.SendAsync(Upload(titleId, await ETagAsync(client, titleId)));
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        var etag = await ETagAsync(client, titleId);

        using var enrichment = await client.GetAsync($"/api/titles/{titleId}/enrichment");
        enrichment.Headers.ETag!.Tag.ShouldBe(etag);
        using (var body = JsonDocument.Parse(await enrichment.Content.ReadAsStringAsync()))
            $"\"{body.RootElement.GetProperty("revision").GetString()}\"".ShouldBe(etag);

        // The title validator does not version these representations, so a matching If-None-Match is still 200.
        foreach (var path in new[]
                 {
                     $"/api/titles/{titleId}/enrichment", $"/api/titles/{titleId}/media",
                     uploaded.Headers.Location!.OriginalString
                 })
        {
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", etag).ShouldBeTrue();
            using var response = await client.SendAsync(conditional);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            response.Headers.ETag!.Tag.ShouldBe(etag, path);
        }
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("fieldOverrides")]
    public async Task Write_IfMatch_MissingIs428_StaleIs412_CurrentSavesAndChainsTheNewETag(string write)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var etag = await ETagAsync(client, titleId);

        using (var missing = await client.SendAsync(CoreWrite(titleId, write, null)))
        using (await ProblemEnvelope.ReadAsync(missing, 428, "Titles.PreconditionRequired"))
        { }
        using (var wildcard = await client.SendAsync(CoreWrite(titleId, write, "*")))
        using (await ProblemEnvelope.ReadAsync(wildcard, 428, "Titles.PreconditionRequired"))
        { }
        using (var stale = await client.SendAsync(CoreWrite(titleId, write, $"\"{Guid.NewGuid()}\"")))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "Titles.PreconditionFailed"))
        { }
        (await GenreAsync(titleId)).ShouldBeNull();
        (await ETagAsync(client, titleId)).ShouldBe(etag);

        using var current = await client.SendAsync(CoreWrite(titleId, write, etag));
        current.StatusCode.ShouldBe(HttpStatusCode.OK, await current.Content.ReadAsStringAsync());
        var next = current.Headers.ETag!.Tag;
        next.ShouldNotBe(etag);
        using (var body = JsonDocument.Parse(await current.Content.ReadAsStringAsync()))
            $"\"{body.RootElement.GetProperty("revision").GetString()}\"".ShouldBe(next);
        (await ETagAsync(client, titleId)).ShouldBe(next);

        using (var replay = await client.SendAsync(CoreWrite(titleId, write, etag)))
            replay.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var chained = await client.SendAsync(CoreWrite(titleId, write, next));
        chained.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Write_TwoConcurrentSavesFromTheSameETag_OneSucceedsAndOneFailsThePrecondition()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var etag = await ETagAsync(client, titleId);

        // Holds for every interleaving: the later save fails the revision check or the save-time recheck.
        var responses = await Task.WhenAll(
            client.SendAsync(Request(HttpMethod.Patch, $"/api/titles/{titleId}", etag, new { genre = "Adventure" })),
            client.SendAsync(Request(HttpMethod.Patch, $"/api/titles/{titleId}", etag, new { genre = "Puzzle" })));

        try
        {
            responses.Select(response => response.StatusCode).Order()
                .ShouldBe([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed]);
            var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
            using var detail = JsonDocument.Parse(await winner.Content.ReadAsStringAsync());
            (await GenreAsync(titleId)).ShouldBe(detail.RootElement.GetProperty("genre").GetString());
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task Patch_IsAPartialUpdate_AnAbsentFieldIsUnchangedAndNullClearsThatOverride()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var path = $"/api/titles/{titleId}";
        using var curated = await client.SendAsync(Request(HttpMethod.Patch, path, await ETagAsync(client, titleId), new
        {
            name = "Curated Name",
            description = "Curated description",
            publisher = "Curated Publisher",
            genre = "Puzzle"
        }));
        curated.StatusCode.ShouldBe(HttpStatusCode.OK, await curated.Content.ReadAsStringAsync());

        using var genre = await client.SendAsync(Request(HttpMethod.Patch, path, curated.Headers.ETag!.Tag,
            new { genre = "x" }));

        genre.StatusCode.ShouldBe(HttpStatusCode.OK, await genre.Content.ReadAsStringAsync());
        var user = await UserLayerAsync(titleId);
        (user.Name, user.Description, user.Publisher, user.Genre)
            .ShouldBe(("Curated Name", "Curated description", "Curated Publisher", "x"));

        using var cleared = await client.SendAsync(Request(HttpMethod.Patch, path, genre.Headers.ETag!.Tag,
            new Dictionary<string, object?> { ["publisher"] = null }));

        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        user = await UserLayerAsync(titleId);
        (user.Name, user.Description, user.Publisher, user.Genre)
            .ShouldBe(("Curated Name", "Curated description", (string?)null, "x"));
        (await EffectiveAsync(titleId)).ShouldBe(("Curated description", (string?)null, "x"));
    }

    [Fact]
    public async Task Media_UploadReadPrimaryAndDelete_EachChainTheTitleETag()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var etag = await ETagAsync(client, titleId);

        using (var unconditional = await client.SendAsync(Upload(titleId, null)))
        using (await ProblemEnvelope.ReadAsync(unconditional, 428, "Titles.PreconditionRequired"))
        { }
        using (var staleUpload = await client.SendAsync(Upload(titleId, $"\"{Guid.NewGuid()}\"")))
        using (await ProblemEnvelope.ReadAsync(staleUpload, 412, "Titles.PreconditionFailed"))
        { }
        (await ETagAsync(client, titleId)).ShouldBe(etag);
        using var uploaded = await client.SendAsync(Upload(titleId, etag));
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        var mediaId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        uploaded.Headers.Location!.OriginalString.ShouldBe($"/api/titles/{titleId}/media/{mediaId}");
        var afterUpload = uploaded.Headers.ETag!.Tag;
        afterUpload.ShouldNotBe(etag);
        (await ETagAsync(client, titleId)).ShouldBe(afterUpload);

        using (var item = await client.GetAsync(uploaded.Headers.Location))
        {
            item.StatusCode.ShouldBe(HttpStatusCode.OK, await item.Content.ReadAsStringAsync());
            (await item.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString().ShouldBe(mediaId);
            item.Headers.ETag!.Tag.ShouldBe(afterUpload);
        }
        using (var unknown = await client.GetAsync($"/api/titles/{titleId}/media/{IdCoder.Encode(int.MaxValue)}"))
        using (await ProblemEnvelope.ReadAsync(unknown, 404, "Catalog.MediaNotFound"))
        { }

        // A retry of the same content writes nothing: the existing item and the unchanged validator.
        using var replayed = await client.SendAsync(Upload(titleId, afterUpload));
        replayed.StatusCode.ShouldBe(HttpStatusCode.OK, await replayed.Content.ReadAsStringAsync());
        (await replayed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString().ShouldBe(mediaId);
        var afterReplay = replayed.Headers.ETag!.Tag;
        afterReplay.ShouldBe(afterUpload);
        (await ETagAsync(client, titleId)).ShouldBe(afterUpload);
        using (var staleReplay = await client.SendAsync(Upload(titleId, etag)))
        using (await ProblemEnvelope.ReadAsync(staleReplay, 412, "Titles.PreconditionFailed"))
        { }

        using (var stale = await client.SendAsync(Request(HttpMethod.Patch, $"/api/titles/{titleId}/media/{mediaId}",
                   etag, new { isPrimary = true })))
            stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using (var demote = await client.SendAsync(Request(HttpMethod.Patch, $"/api/titles/{titleId}/media/{mediaId}",
                   afterReplay, new { isPrimary = false })))
        using (await ProblemEnvelope.ReadAsync(demote, 400, "Titles.InvalidMediaUpdate"))
        { }
        using var primary = await client.SendAsync(Request(HttpMethod.Patch, $"/api/titles/{titleId}/media/{mediaId}",
            afterReplay, new { isPrimary = true }));
        primary.StatusCode.ShouldBe(HttpStatusCode.OK, await primary.Content.ReadAsStringAsync());
        var afterPrimary = primary.Headers.ETag!.Tag;

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete, $"/api/titles/{titleId}/media/{mediaId}",
            afterPrimary));
        deleted.StatusCode.ShouldBe(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        using (var detail = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync()))
        {
            var media = detail.RootElement.GetProperty("media");
            media.GetProperty("items").GetArrayLength().ShouldBe(0);
            media.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
            media.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        }
        deleted.Headers.ETag!.Tag.ShouldNotBe(afterPrimary);
    }

    [Fact]
    public async Task Merge_TargetIsTheIfMatchResource_SourceRevisionIsABatchRevision()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var targetId = await SeedTitleAsync();
        var sourceId = await SeedTitleAsync();
        var targetTag = await ETagAsync(client, targetId);
        var sourceRevision = (await StoredRevisionAsync(sourceId)).ToString();

        using (var staleTarget = await client.SendAsync(Merge(targetId, $"\"{Guid.NewGuid()}\"", sourceId, sourceRevision)))
        using (await ProblemEnvelope.ReadAsync(staleTarget, 412, "Titles.PreconditionFailed"))
        { }
        using (var staleSource = await client.SendAsync(Merge(targetId, targetTag, sourceId, Guid.NewGuid().ToString())))
        using (var problem = await ProblemEnvelope.ReadAsync(staleSource, 409, "Titles.MergeSourceChanged"))
            problem.RootElement.GetProperty("errors").TryGetProperty("sourceTitleRevision", out _).ShouldBeTrue();

        using var merged = await client.SendAsync(Merge(targetId, targetTag, sourceId, sourceRevision));

        merged.StatusCode.ShouldBe(HttpStatusCode.OK, await merged.Content.ReadAsStringAsync());
        merged.Headers.ETag!.Tag.ShouldNotBe(targetTag);
        (await client.GetAsync($"/api/titles/{sourceId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MoveGame_IsUnconditional_AndOnlyMovesAGameOfThePathTitle()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, gameId, targetId) = await SeedTitleWithGameAsync();

        // A move changes no title row, so it is outside the title family: no If-Match, and no ETag back.
        using var notThisTitlesGame = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/titles/{targetId}/games/{gameId}/move", null, new { targetTitleId = titleId }));
        using (await ProblemEnvelope.ReadAsync(notThisTitlesGame, 404, "Catalog.GameNotFound"))
        { }
        notThisTitlesGame.Headers.ETag.ShouldBeNull();

        using var moved = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/titles/{titleId}/games/{gameId}/move", null, new { targetTitleId = targetId }));

        moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        moved.Headers.ETag.ShouldBeNull();
        (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gameId").GetString().ShouldBe(gameId);
    }

    [Fact]
    public async Task Enrich_CurrentIfMatch_Is202WithoutAnETagAndRotatesTheRevision()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var revision = await StoredRevisionAsync(titleId);

        using var accepted = await client.SendAsync(Request(HttpMethod.Post, $"/api/titles/{titleId}/enrich",
            $"\"{revision}\""));

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        accepted.Headers.ETag.ShouldBeNull();
        (await StoredRevisionAsync(titleId)).ShouldNotBe(revision);
    }

    [Fact]
    public async Task Tracking_IsUnconditional_AndTheBatchIsABatchAction()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();

        (await client.PutAsJsonAsync($"/api/titles/{titleId}/tracking", new { tracked = true }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync("/api/titles/batch-set-tracking", new { titleIds = new[] { titleId }, tracked = false }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("PUT", "content-ratings/Esrb")]
    [InlineData("DELETE", "content-ratings/Esrb")]
    [InlineData("POST", "merge")]
    [InlineData("POST", "enrich")]
    public async Task FamilyWrite_MissingIfMatchIs428_StaleIs412_AndNeitherWrites(string method, string suffix)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var etag = await ETagAsync(client, titleId);
        var sourceId = await SeedTitleAsync();
        object? body = suffix switch
        {
            "content-ratings/Esrb" when method == "PUT" => new { code = "E" },
            "merge" => new { sourceTitleId = sourceId, sourceTitleRevision = (await StoredRevisionAsync(sourceId)).ToString() },
            _ => null
        };
        var path = $"/api/titles/{titleId}/{suffix}";

        using (var missing = await client.SendAsync(Request(new HttpMethod(method), path, null, body)))
        using (await ProblemEnvelope.ReadAsync(missing, 428, "Titles.PreconditionRequired"))
        { }
        using (var stale = await client.SendAsync(Request(new HttpMethod(method), path, $"\"{Guid.NewGuid()}\"", body)))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "Titles.PreconditionFailed"))
        { }

        (await ETagAsync(client, titleId)).ShouldBe(etag);
    }

    [Fact]
    public async Task UnknownTitleOrMedia_Is404WhoseDetailNamesNoInternalId()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var missing = IdCoder.Encode(int.MaxValue);

        using (var title = await client.SendAsync(Request(HttpMethod.Delete, $"/api/titles/{missing}/media/{IdCoder.Encode(1)}",
                   $"\"{Guid.NewGuid()}\"")))
        using (var problem = await ProblemEnvelope.ReadAsync(title, 404, "Catalog.TitleNotFound"))
            problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain(int.MaxValue.ToString());
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
        {
            using var media = await client.SendAsync(Request(method, $"/api/titles/{titleId}/media/{missing}",
                method == HttpMethod.Get ? null : await ETagAsync(client, titleId)));
            using var problem = await ProblemEnvelope.ReadAsync(media, 404, "Catalog.MediaNotFound");
            problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain(int.MaxValue.ToString());
        }
    }

    [Theory]
    [InlineData("PATCH", "")]
    [InlineData("PUT", "/content-ratings/Esrb")]
    [InlineData("DELETE", "/content-ratings/Esrb")]
    [InlineData("PATCH", "/media/{mediaId}")]
    [InlineData("PUT", "/tracking")]
    [InlineData("GET", "/source-references")]
    [InlineData("POST", "/enrich")]
    [InlineData("GET", "/enrichment")]
    [InlineData("PATCH", "/enrichment")]
    [InlineData("PUT", "/enrichment-omissions/Genre")]
    [InlineData("GET", "/enrichment-assessment")]
    [InlineData("GET", "/artwork-acquisition")]
    [InlineData("POST", "/artwork-acquisition/fill")]
    public async Task UnknownTitle_OnATitleFamilyRequest_Is404WhoseDetailHasNoDigits(string method, string suffix)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var missing = IdCoder.Encode(int.MaxValue);
        object? body = suffix switch
        {
            "" => new { genre = "Adventure" },
            "/content-ratings/Esrb" when method == "PUT" => new { code = "E" },
            "/media/{mediaId}" => new { isPrimary = true },
            "/tracking" => new { tracked = true },
            "/enrichment" when method == "PATCH" => new { overrides = new Dictionary<string, string?> { ["Genre"] = "user" } },
            "/enrichment-omissions/Genre" => new { accepted = true },
            _ => null
        };
        var path = $"/api/titles/{missing}{suffix.Replace("{mediaId}", IdCoder.Encode(1))}";

        using var response = await client.SendAsync(Request(new HttpMethod(method), path,
            method == "GET" ? null : $"\"{Guid.NewGuid()}\"", body));

        // The decoded id is internal, so the detail carries no number at all.
        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Catalog.TitleNotFound");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().Any(char.IsDigit).ShouldBeFalse();
    }

    [Theory]
    [InlineData("GET", "/api/titles/{titleId}/details")]
    [InlineData("PATCH", "/api/titles/{titleId}/metadata")]
    [InlineData("PATCH", "/api/titles/{titleId}/content-rating")]
    [InlineData("PATCH", "/api/titles/{titleId}/enrichment/fields")]
    [InlineData("POST", "/api/titles/{titleId}/external-ids")]
    [InlineData("PUT", "/api/titles/{titleId}/external-ids/igdb")]
    [InlineData("POST", "/api/titles/{titleId}/external-ids/igdb/confirm")]
    [InlineData("POST", "/api/titles/merge")]
    [InlineData("PUT", "/api/titles/{titleId}/media/{mediaId}/primary")]
    [InlineData("PUT", "/api/titles/tracking")]
    [InlineData("POST", "/api/titles/games/{gameId}/move")]
    [InlineData("GET", "/api/enrichment/workbench/{titleId}")]
    [InlineData("PUT", "/api/enrichment/workbench/{titleId}/omissions/Genre")]
    [InlineData("PUT", "/api/titles/{titleId}/enrichment/omissions/Genre")]
    public async Task RetiredTitleRoutes_AnswerOnlyTheRoutingProblem(string method, string template)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var path = template.Replace("{titleId}", titleId)
            .Replace("{mediaId}", IdCoder.Encode(1)).Replace("{gameId}", IdCoder.Encode(1));

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { })
        });

        // Routing's own problem, so a route that still existed could not pass with a handler's 404.
        ((int)response.StatusCode).ShouldBeOneOf(404, 405);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("errorCode").GetString()
            .ShouldBeOneOf("Request.RouteNotFound", "Request.MethodNotAllowed");
    }

    private static HttpRequestMessage CoreWrite(string titleId, string write, string? ifMatch) => write == "metadata"
        ? Request(HttpMethod.Patch, $"/api/titles/{titleId}", ifMatch, new { genre = "Adventure" })
        : Request(HttpMethod.Patch, $"/api/titles/{titleId}/enrichment", ifMatch,
            new { overrides = new Dictionary<string, string?> { ["Genre"] = "user" } });

    private static HttpRequestMessage Merge(string targetId, string ifMatch, string sourceId, string sourceRevision) =>
        Request(HttpMethod.Post, $"/api/titles/{targetId}/merge", ifMatch,
            new { sourceTitleId = sourceId, sourceTitleRevision = sourceRevision });

    private static HttpRequestMessage Upload(string titleId, string? ifMatch)
    {
        var file = new ByteArrayContent(PngHeader);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/titles/{titleId}/media?type=cover")
        {
            Content = new MultipartFormDataContent { { file, "file", "cover.png" } }
        };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
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

    private static async Task<string> ETagAsync(HttpClient client, string titleId)
    {
        using var response = await client.GetAsync($"/api/titles/{titleId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private async Task<Guid> StoredRevisionAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Titles
            .Where(title => title.Id == id).Select(title => title.Revision).SingleAsync();
    }

    private async Task<string?> GenreAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Titles
            .Where(title => title.Id == id).Select(title => title.Genre).SingleAsync();
    }

    private async Task<TitleMetadataPayload> UserLayerAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        var json = await scope.ServiceProvider.GetRequiredService<RomdDbContext>().TitleMetadataLayers
            .Where(layer => layer.TitleId == id && layer.SourceId == "user").Select(layer => layer.MetadataJson)
            .SingleAsync();
        return JsonSerializer.Deserialize<TitleMetadataPayload>(json, MetadataJsonOptions.Default).ShouldNotBeNull();
    }

    private async Task<(string? Description, string? Publisher, string? Genre)> EffectiveAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        var title = await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Titles
            .SingleAsync(title => title.Id == id);
        return (title.Description, title.Publisher, title.Genre);
    }

    /// <summary>A title with one DAT game on its own platform, and a second title to move it to.</summary>
    private async Task<(string TitleId, string GameId, string TargetId)> SeedTitleWithGameAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var platform = new PlatformEntity
        {
            Name = $"Move Platform {suffix}",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation,
            BaseName = $"Move Platform {suffix}",
            BaseCompactLabel = "Move",
            CanonicalKey = $"local-mv-{suffix}",
            ShortName = $"mv-{suffix}"
        };
        var file = new FileEntityPersistence
        {
            Sha256 = Sha256.FromBytes(RandomNumberGenerator.GetBytes(Sha256.ByteLength)),
            Size = 1,
            SizeOnDisk = 1
        };
        db.Platforms.Add(platform);
        db.Files.Add(file);
        await db.SaveChangesAsync();
        var catalogSource = new CatalogSourceEntity
        {
            Kind = nameof(CatalogSourceKind.Dat),
            Status = nameof(CatalogSourceStatus.Active)
        };
        var datFile = new DatFileEntity
        {
            Source = new DatSourceEntity { CatalogSource = catalogSource },
            Name = $"Move DAT {suffix}",
            Description = $"Move DAT {suffix}",
            Type = nameof(DatType.NoIntro),
            PlatformId = platform.Id,
            OriginalFilename = "move.dat",
            FileId = file.Id,
            Lifecycle = nameof(DatFileLifecycle.Active)
        };
        TitleEntity NewTitle(string name) => new()
        {
            PlatformId = platform.Id,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var title = NewTitle($"Move Source {suffix}");
        var target = NewTitle($"Move Target {suffix}");
        db.DatFiles.Add(datFile);
        db.Titles.AddRange(title, target);
        await db.SaveChangesAsync();
        var entry = new SourceEntryEntity
        {
            CatalogSourceId = catalogSource.Id,
            EntryKey = $"Move Game {suffix}",
            Name = $"Move Game {suffix}",
            PlatformId = platform.Id
        };
        db.SourceEntries.Add(entry);
        await db.SaveChangesAsync();
        var game = new DatGameEntity
        {
            DatFileId = datFile.Id,
            SourceEntryId = entry.Id,
            Name = entry.Name,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.DatGames.Add(game);
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = entry.Id, TitleId = title.Id });
        await db.SaveChangesAsync();
        return (IdCoder.Encode(title.Id), IdCoder.Encode(game.Id), IdCoder.Encode(target.Id));
    }

    private async Task<string> SeedTitleAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Concurrency Test Title {Guid.NewGuid():N}";
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
}
