using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Hashing;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Regression coverage for the admin host's legacy title media route (<c>GET /media/{mediaId}</c>)
///     over real persistence and content-addressable storage with synthetic bytes.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class StorageEndpointsTests : IDisposable
{
    private readonly HttpClient _anonymous;
    private readonly IntegrationTestFixture _fixture;

    public StorageEndpointsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _anonymous = fixture.CreateClient();
    }

    public void Dispose() => _anonymous.Dispose();

    [Fact]
    public async Task ServeMedia_StoredMedia_ReturnsBytesContentTypeAndImmutableCaching()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(64);
        int mediaId = await SeedTitleMediaAsync(bytes, "image/png");

        using var response = await _anonymous.GetAsync($"/media/{IdCoder.Encode(mediaId)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        var cacheControl = response.Headers.CacheControl.ShouldNotBeNull();
        cacheControl.Public.ShouldBeTrue();
        cacheControl.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        cacheControl.Extensions.ShouldContain(extension => extension.Name == "immutable");
    }

    [Fact]
    public async Task ServeMedia_UnknownMediaId_ReturnsNotFoundWithoutImmutableCaching()
    {
        using var response = await _anonymous.GetAsync($"/media/{IdCoder.Encode(int.MaxValue - 7)}");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Delivery.ContentNotFound");
        body.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain((int.MaxValue - 7).ToString());
        response.Headers.CacheControl?.Extensions.ShouldNotContain(extension => extension.Name == "immutable");
    }

    [Fact]
    public async Task ServeMedia_RecordWithoutStoredBytes_ReturnsNotFoundWithoutImmutableCaching()
    {
        int mediaId = await SeedTitleMediaAsync(content: null, "image/jpeg");

        using var response = await _anonymous.GetAsync($"/media/{IdCoder.Encode(mediaId)}");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Delivery.ContentNotFound");
        response.Headers.CacheControl?.Extensions.ShouldNotContain(extension => extension.Name == "immutable");
    }

    [Fact]
    public async Task ServeMedia_InvalidSqid_ReturnsBadRequest()
    {
        using var response = await _anonymous.GetAsync("/media/not-a-valid-sqid!");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    ///     Seeds a title media row. With <paramref name="content" />, its file row points at those
    ///     bytes in CAS; without it, the file row names a hash that was never stored.
    /// </summary>
    private async Task<int> SeedTitleMediaAsync(byte[]? content, string contentType)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        int platformId = await db.Platforms.OrderBy(platform => platform.Id).Select(platform => platform.Id).FirstAsync();

        Sha256 sha256;
        if (content is null)
        {
            sha256 = Sha256.Parse(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        }
        else
        {
            var store = scope.ServiceProvider.GetRequiredService<IContentAddressableStore>();
            await using var stream = new MemoryStream(content);
            sha256 = (await store.StoreAsync(stream)).Key.Hash;
        }

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        string name = $"Storage Media Title {Guid.NewGuid():N}";
        var title = new TitleEntity
        {
            PlatformId = platformId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = now,
            CreatedByUserId = userId
        };
        var file = new FileEntityPersistence
        {
            Sha256 = sha256,
            Size = content?.Length ?? 1,
            SizeOnDisk = content?.Length ?? 1,
            CreatedAt = now,
            CreatedByUserId = userId
        };
        db.Titles.Add(title);
        db.Files.Add(file);
        await db.SaveChangesAsync();

        var media = new TitleMediaEntity
        {
            TitleId = title.Id,
            Type = "Cover",
            FileId = file.Id,
            SourceId = "user",
            IsPrimary = true,
            ContentType = contentType,
            CreatedAt = now,
            CreatedByUserId = userId
        };
        db.TitleMedia.Add(media);
        await db.SaveChangesAsync();

        return media.Id;
    }
}
