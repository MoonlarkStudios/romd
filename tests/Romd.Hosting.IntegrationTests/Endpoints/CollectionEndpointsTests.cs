using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Hashing;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Contract coverage for the Sqid-typed collection cover media id
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity" and
///     "Strict Validation"): requests carry Sqid strings, and supplied-but-invalid values fail
///     with the shared 400 envelope instead of leaking int identity.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class CollectionEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public CollectionEndpointsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateAuthenticatedClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CreateCollection_ValidCoverMediaSqid_CreatesWithCoverUrl()
    {
        int mediaId = await SeedTitleMediaAsync();
        string coverMediaSqid = IdCoder.Encode(mediaId);

        var response = await _client.PostAsJsonAsync(
            "/api/collections",
            new { name = $"Cover Collection {Guid.NewGuid():N}", coverMediaId = coverMediaSqid });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("coverUrl").GetString().ShouldBe($"/media/{coverMediaSqid}");
    }

    [Fact]
    public async Task CreateCollection_WithoutCoverMediaId_Creates()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/collections",
            new { name = $"Plain Collection {Guid.NewGuid():N}" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("coverUrl").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task CreateCollection_InvalidCoverMediaId_Returns400Envelope()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/collections",
            new { name = "Invalid Cover Collection", coverMediaId = "!!!" });

        await ShouldBeInvalidCoverMediaEnvelopeAsync(response, "!!!");
    }

    [Fact]
    public async Task CreateCollection_ValidPlatformSqid_Creates()
    {
        string systemKey = await TestHelpers.GetFirstSystemKeyAsync(_client);

        var response = await _client.PostAsJsonAsync(
            "/api/collections",
            new { name = $"Platform Collection {Guid.NewGuid():N}", systemKey });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("systemKey").GetString().ShouldBe(systemKey);
    }

    [Fact]
    public async Task CreateCollection_InvalidPlatformId_Returns400Envelope()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/collections",
            new { name = "Invalid Platform Collection", systemKey = "not-a-sqid" });

        await ShouldBeInvalidPlatformIdEnvelopeAsync(response, "not-a-sqid");
    }

    [Fact]
    public async Task UpdateCollection_InvalidPlatformId_Returns400Envelope()
    {
        var createResponse = await _client.PostAsJsonAsync(
            "/api/collections",
            new { name = $"Platform Update Target {Guid.NewGuid():N}" });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        string collectionId = created.RootElement.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync(
            $"/api/collections/{collectionId}",
            new { name = "Renamed Platform Collection", systemKey = "!!!" });

        await ShouldBeInvalidPlatformIdEnvelopeAsync(response, "!!!");
    }

    [Fact]
    public async Task UpdateCollection_InvalidCoverMediaId_Returns400Envelope()
    {
        var createResponse = await _client.PostAsJsonAsync(
            "/api/collections",
            new { name = $"Update Target Collection {Guid.NewGuid():N}" });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        string collectionId = created.RootElement.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync(
            $"/api/collections/{collectionId}",
            new { name = "Renamed Collection", coverMediaId = "not-a-sqid" });

        await ShouldBeInvalidCoverMediaEnvelopeAsync(response, "not-a-sqid");
    }

    private static async Task ShouldBeInvalidPlatformIdEnvelopeAsync(
        HttpResponseMessage response,
        string suppliedValue)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Collections.InvalidSystemKey");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errors").GetProperty("systemKey")[0].GetString()
            .ShouldNotBeNull().ShouldContain(suppliedValue);
    }

    private static async Task ShouldBeInvalidCoverMediaEnvelopeAsync(
        HttpResponseMessage response,
        string suppliedValue)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Collections.InvalidCoverMediaId");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errors").GetProperty("coverMediaId")[0].GetString()
            .ShouldNotBeNull().ShouldContain(suppliedValue);
    }

    /// <summary>Seeds a title, a stored file, and a cover media row; returns the media id.</summary>
    private async Task<int> SeedTitleMediaAsync()
    {
        string systemKey = await TestHelpers.GetFirstSystemKeyAsync(_client);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var platformId = await db.Platforms.Where(x => x.CanonicalKey == systemKey).Select(x => x.Id).SingleAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        string name = $"Cover Media Test Title {Guid.NewGuid():N}";
        var title = new TitleEntity
        {
            PlatformId = platformId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = now,
            CreatedByUserId = userId
        };
        db.Titles.Add(title);

        var file = new FileEntityPersistence
        {
            Sha256 = Sha256.Parse($"{Guid.NewGuid():N}{Guid.NewGuid():N}"),
            Size = 1024,
            SizeOnDisk = 1024,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = userId
        };
        db.Files.Add(file);
        await db.SaveChangesAsync();

        var media = new TitleMediaEntity
        {
            TitleId = title.Id,
            Type = "Cover",
            FileId = file.Id,
            SourceId = "user",
            IsPrimary = true,
            ContentType = "image/jpeg",
            CreatedAt = now,
            CreatedByUserId = userId
        };
        db.TitleMedia.Add(media);
        await db.SaveChangesAsync();

        return media.Id;
    }
}
