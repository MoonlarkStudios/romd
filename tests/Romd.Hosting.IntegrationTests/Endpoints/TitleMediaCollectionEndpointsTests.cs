using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;
using Microsoft.EntityFrameworkCore;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Collecting packaging scans and manuals into a title's library: release placement and per-release preference,
///     classification changes under the title validator, and manual delivery through the document representation
///     rather than the anonymous image lane.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TitleMediaCollectionEndpointsTests(IntegrationTestFixture fixture)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    [Fact]
    public async Task RegionalBoxFronts_OnePreferredPerRelease_AndAlternativesAreRetained()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, usa, europe) = await SeedTitleWithReleasesAsync();

        var usaScan = await UploadAsync(client, titleId, "BoxFront", Png, "usa.png", usa, "USA");
        var europeScan = await UploadAsync(client, titleId, "BoxFront", Jpeg, "europe.jpg", europe, "Europe");
        var usaAlternative = await UploadAsync(client, titleId, "BoxFront", [.. Png, 0x01], "usa-alt.png", usa, "USA");

        usaScan.GetProperty("catalogReleaseId").GetString().ShouldBe(usa);
        usaScan.GetProperty("region").GetString().ShouldBe("USA");
        usaScan.GetProperty("isPreferred").GetBoolean().ShouldBeFalse();

        (await ClassifyAsync(client, titleId, Id(usaScan), new { type = "BoxFront", catalogReleaseId = usa, region = "USA", isPreferred = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ClassifyAsync(client, titleId, Id(europeScan), new { type = "BoxFront", catalogReleaseId = europe, region = "Europe", language = "French", isPreferred = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        using var switched = await ClassifyAsync(client, titleId, Id(usaAlternative),
            new { type = "BoxFront", catalogReleaseId = usa, region = "USA", isPreferred = true });
        switched.StatusCode.ShouldBe(HttpStatusCode.OK, await switched.Content.ReadAsStringAsync());

        var media = await ListMediaAsync(client, titleId);
        media.Count.ShouldBe(3);
        Preferred(media, usa).ShouldBe(Id(usaAlternative));
        Preferred(media, europe).ShouldBe(Id(europeScan));
        media.Single(item => Id(item) == Id(europeScan)).GetProperty("language").GetString().ShouldBe("French");
        media.Single(item => Id(item) == Id(usaScan)).GetProperty("isPreferred").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Classify_ToAnotherTitlesRelease_Is400KeyedByTheRelease_AndLeavesTheItem()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, _, _) = await SeedTitleWithReleasesAsync();
        var (_, foreign, _) = await SeedTitleWithReleasesAsync();
        var scan = await UploadAsync(client, titleId, "DiscLabel", Png, "disc.png");

        using var response = await ClassifyAsync(client, titleId, Id(scan),
            new { type = "DiscLabel", catalogReleaseId = foreign, isPreferred = true });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Catalog.MediaReleaseNotFound");
        problem.RootElement.GetProperty("errors").TryGetProperty("catalogReleaseId", out _).ShouldBeTrue();
        (await ListMediaAsync(client, titleId)).Single().GetProperty("catalogReleaseId").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Upload_ToAnotherTitlesRelease_Is400AndStoresNothing()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, _, _) = await SeedTitleWithReleasesAsync();
        var (_, foreign, _) = await SeedTitleWithReleasesAsync();

        using var response = await client.SendAsync(Upload(titleId, "BoxBack", Png, "back.png",
            await ETagAsync(client, titleId), foreign));

        await ProblemEnvelope.ReadAsync(response, 400, "Catalog.MediaReleaseNotFound");
        (await ListMediaAsync(client, titleId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Classify_PreferenceOutsidePackaging_Is400_AndManualCannotBecomeAnImage()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, usa, _) = await SeedTitleWithReleasesAsync();
        var screenshot = await UploadAsync(client, titleId, "Screenshot", Png, "shot.png");
        var manual = await UploadAsync(client, titleId, "Manual", Pdf(), "manual.pdf", usa, "USA", "English");

        await ProblemEnvelope.ReadAsync(await ClassifyAsync(client, titleId, Id(screenshot),
            new { type = "Screenshot", isPreferred = true }), 400, "Catalog.MediaPreferenceNotSupported");
        await ProblemEnvelope.ReadAsync(await ClassifyAsync(client, titleId, Id(manual),
            new { type = "BoxFront", catalogReleaseId = usa }), 409, "Catalog.MediaTypeLocked");
        await ProblemEnvelope.ReadAsync(await ClassifyAsync(client, titleId, Id(manual),
            new { type = "NotAType" }), 400, "Titles.InvalidMediaUpdate");
    }

    [Fact]
    public async Task Classify_StaleTitleValidator_Is412()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, _, _) = await SeedTitleWithReleasesAsync();
        var stale = await ETagAsync(client, titleId);
        var scan = await UploadAsync(client, titleId, "BoxSpine", Png, "spine.png");

        using var request = Patch(titleId, Id(scan), stale, new { classification = new { type = "BoxSpine", isPreferred = true } });
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Manual_IsOneLibraryEntry_DeliveredOnlyAsAnAuthenticatedSandboxedDocument()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, usa, _) = await SeedTitleWithReleasesAsync();
        var pdf = Pdf();

        var manual = await UploadAsync(client, titleId, "Manual", pdf, "manual.pdf", usa, "USA", "English");

        manual.GetProperty("type").GetString().ShouldBe("Manual");
        manual.GetProperty("contentType").GetString().ShouldBe("application/pdf");
        manual.GetProperty("language").GetString().ShouldBe("English");
        (await ListMediaAsync(client, titleId)).Count.ShouldBe(1);

        using (var anonymous = fixture.CreateClient())
        using (var image = await anonymous.GetAsync($"/media/{Id(manual)}"))
            image.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var document = new HttpRequestMessage(HttpMethod.Get, $"/api/titles/{titleId}/media/{Id(manual)}");
        document.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        using var delivered = await client.SendAsync(document);
        delivered.StatusCode.ShouldBe(HttpStatusCode.OK, await delivered.Content.ReadAsStringAsync());
        delivered.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        delivered.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        delivered.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        delivered.Headers.GetValues("Content-Security-Policy").Single().ShouldStartWith("sandbox");
        (await delivered.Content.ReadAsByteArrayAsync()).ShouldBe(pdf);

        using var json = await client.GetAsync($"/api/titles/{titleId}/media/{Id(manual)}");
        json.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var (otherTitle, _, _) = await SeedTitleWithReleasesAsync();
        using var foreign = new HttpRequestMessage(HttpMethod.Get, $"/api/titles/{otherTitle}/media/{Id(manual)}");
        foreign.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        (await client.SendAsync(foreign)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ImageMedia_IsNeverServedAsADocument()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, _, _) = await SeedTitleWithReleasesAsync();
        var scan = await UploadAsync(client, titleId, "BoxFront", Png, "front.png");

        using var document = new HttpRequestMessage(HttpMethod.Get, $"/api/titles/{titleId}/media/{Id(scan)}");
        document.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));

        (await client.SendAsync(document)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Manual", "not a pdf", "Catalog.InvalidManual")]
    [InlineData("BoxFront", "%PDF-1.7 not an image", "Catalog.InvalidMediaContent")]
    public async Task Upload_ContentThatDoesNotMatchItsType_Is400KeyedByTheFile(string type, string content, string code)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, _, _) = await SeedTitleWithReleasesAsync();

        using var response = await client.SendAsync(Upload(titleId, type, Encoding.ASCII.GetBytes(content), "file.bin",
            await ETagAsync(client, titleId)));

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, code);
        problem.RootElement.GetProperty("errors").TryGetProperty("file", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Upload_OverlongRegion_Is400()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (titleId, _, _) = await SeedTitleWithReleasesAsync();

        using var response = await client.SendAsync(Upload(titleId, "BoxFront", Png, "front.png",
            await ETagAsync(client, titleId), region: new string('r', 101)));

        await ProblemEnvelope.ReadAsync(response, 400, "Catalog.InvalidMediaPlacement");
    }

    private static string Id(JsonElement media) => media.GetProperty("id").GetString()!;

    private static string? Preferred(IEnumerable<JsonElement> media, string releaseId) => media
        .Where(item => item.GetProperty("catalogReleaseId").GetString() == releaseId &&
                       item.GetProperty("isPreferred").GetBoolean())
        .Select(Id).SingleOrDefault();

    private async Task<JsonElement> UploadAsync(HttpClient client, string titleId, string type, byte[] content,
        string name, string? releaseId = null, string? region = null, string? language = null)
    {
        using var response = await client.SendAsync(Upload(titleId, type, content, name, await ETagAsync(client, titleId),
            releaseId, region, language));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private static HttpRequestMessage Upload(string titleId, string type, byte[] content, string name, string ifMatch,
        string? releaseId = null, string? region = null, string? language = null)
    {
        var query = new StringBuilder($"type={type}");
        if (releaseId is not null)
            query.Append("&releaseId=").Append(Uri.EscapeDataString(releaseId));
        if (region is not null)
            query.Append("&region=").Append(Uri.EscapeDataString(region));
        if (language is not null)
            query.Append("&language=").Append(Uri.EscapeDataString(language));
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/titles/{titleId}/media?{query}")
        {
            Content = new MultipartFormDataContent { { file, "file", name } }
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    private static async Task<HttpResponseMessage> ClassifyAsync(HttpClient client, string titleId, string mediaId,
        object classification)
    {
        using var request = Patch(titleId, mediaId, await ETagAsync(client, titleId), new { classification });
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Patch(string titleId, string mediaId, string ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/titles/{titleId}/media/{mediaId}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    private static async Task<List<JsonElement>> ListMediaAsync(HttpClient client, string titleId)
    {
        using var response = await client.GetAsync($"/api/titles/{titleId}/media?limit=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private static async Task<string> ETagAsync(HttpClient client, string titleId)
    {
        using var response = await client.GetAsync($"/api/titles/{titleId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.Tag;
    }

    private async Task<(string TitleId, string Usa, string Europe)> SeedTitleWithReleasesAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Collection Test Title {Guid.NewGuid():N}";
        var platformId = await db.Platforms.OrderBy(platform => platform.Id).Select(platform => platform.Id).FirstAsync();
        var title = new TitleEntity
        {
            PlatformId = platformId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var usa = new CatalogReleaseEntity
        {
            PlatformId = platformId,
            CatalogTitleId = title.Id,
            Fingerprint = $"fp:{Guid.NewGuid():N}",
            Name = $"{name} (USA)",
            Region = "USA",
            CreatedAt = now,
            UpdatedAt = now
        };
        var europe = new CatalogReleaseEntity
        {
            PlatformId = platformId,
            CatalogTitleId = title.Id,
            Fingerprint = $"fp:{Guid.NewGuid():N}",
            Name = $"{name} (Europe)",
            Region = "Europe",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.CatalogReleases.AddRange(usa, europe);
        await db.SaveChangesAsync();
        return (IdCoder.Encode(title.Id), IdCoder.Encode(usa.Id), IdCoder.Encode(europe.Id));
    }

    /// <summary>A minimal one-page PDF with a classic cross-reference table.</summary>
    private static byte[] Pdf()
    {
        var body = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var content in new[]
                 {
                     "<< /Type /Catalog /Pages 2 0 R >>",
                     "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"
                 })
        {
            offsets.Add(body.Length);
            body.Append($"{offsets.Count} 0 obj\n{content}\nendobj\n");
        }

        var xref = body.Length;
        body.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            body.Append($"{offset:D10} 00000 n \n");
        body.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }
}
