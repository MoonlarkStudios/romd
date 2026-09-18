using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Management.Commands;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class BatchDeleteRomsEndpointsTests(IntegrationTestFixture fixture)
{
    private const long UnsafeJavaScriptInteger = 9_007_199_254_740_993;

    [Fact]
    public async Task BatchDelete_ListedPublicIds_DeletesEveryRequestedRom()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string prefix = $"batch-public-{Guid.NewGuid():N}";
        var seeded = await SeedRomsAsync(prefix, 2, firstSize: UnsafeJavaScriptInteger);
        IReadOnlyList<string> publicIds = await ListPublicIdsAsync(client, seeded);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/roms/batch")
        {
            Content = JsonContent.Create(new BatchDeleteRomsRequest { RomIds = publicIds })
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("deletedCount").GetInt32().ShouldBe(2);
        document.RootElement.GetProperty("failedCount").GetInt32().ShouldBe(0);
        await AssertRomsExistAsync(seeded.Select(item => item.Id), expected: false);
    }

    [Fact]
    public async Task BatchDelete_AnyMalformedPublicId_RejectsBeforeDeletingValidIds()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string prefix = $"batch-invalid-{Guid.NewGuid():N}";
        var seeded = await SeedRomsAsync(prefix, 1);
        string publicId = (await ListPublicIdsAsync(client, seeded)).Single();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/roms/batch")
        {
            Content = JsonContent.Create(new BatchDeleteRomsRequest
            {
                RomIds = [publicId, "not-a-sqid"]
            })
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe("Roms.InvalidRomId");
        await AssertRomsExistAsync(seeded.Select(item => item.Id), expected: true);
    }

    [Fact]
    public async Task BatchDelete_DuplicatePublicId_IsRejectedWithoutDeletingTheRom()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var seeded = await SeedRomsAsync($"batch-duplicate-{Guid.NewGuid():N}", 1);
        string publicId = (await ListPublicIdsAsync(client, seeded)).Single();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/roms/batch")
        {
            Content = JsonContent.Create(new BatchDeleteRomsRequest { RomIds = [publicId, publicId] })
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe("Roms.InvalidRomIds");
        await AssertRomsExistAsync(seeded.Select(item => item.Id), expected: true);
    }

    [Theory]
    [InlineData("{}", "Request.InvalidBody")]
    [InlineData("{\"romIds\":null}", "Roms.InvalidRomIds")]
    [InlineData("{\"romIds\":[]}", "Roms.InvalidRomIds")]
    public async Task BatchDelete_MissingNullOrEmptyIds_IsRejected(string json, string expectedErrorCode)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/roms/batch")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe(expectedErrorCode);
    }

    [Fact]
    public async Task BatchDelete_NullListElement_IsRejectedAsAnInvalidPublicId()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/roms/batch")
        {
            Content = new StringContent(
                "{\"romIds\":[null]}",
                System.Text.Encoding.UTF8,
                "application/json")
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe("Roms.InvalidRomId");
    }

    [Fact]
    public async Task BatchDelete_ExactlyOneThousandDistinctIds_IsAccepted()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string[] publicIds = Enumerable.Range(0, 1000)
            .Select(offset => Romd.Application.Common.Ids.IdCoder.Encode(int.MaxValue - offset))
            .ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/roms/batch")
        {
            Content = JsonContent.Create(new BatchDeleteRomsRequest { RomIds = publicIds })
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("deletedCount").GetInt32().ShouldBe(1000);
        document.RootElement.GetProperty("failedCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task BatchDelete_MoreThanOneThousandIds_IsRejected()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string publicId = Romd.Application.Common.Ids.IdCoder.Encode(1);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/roms/batch")
        {
            Content = JsonContent.Create(new BatchDeleteRomsRequest
            {
                RomIds = Enumerable.Repeat(publicId, 1001).ToArray()
            })
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("errorCode").GetString().ShouldBe("Roms.InvalidRomIds");
    }

    private async Task<IReadOnlyList<(int Id, string FileName, long Size)>> SeedRomsAsync(
        string prefix,
        int count,
        long firstSize = 1)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var result = new List<(int Id, string FileName, long Size)>();

        for (int index = 0; index < count; index++)
        {
            string seed = Guid.NewGuid().ToString("N");
            long size = checked(firstSize + index);
            var file = new FileEntityPersistence
            {
                Sha256 = Sha256.Parse(seed + seed),
                Size = size,
                SizeOnDisk = size,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = Guid.NewGuid()
            };
            context.Files.Add(file);
            await context.SaveChangesAsync();

            string fileName = $"{prefix}-{index}.rom";
            var rom = new RomFileEntity
            {
                OriginalFilename = fileName,
                FileId = file.Id,
                Sha1 = Sha1.Parse(seed + seed[..8]),
                Md5 = Md5.Parse(seed),
                Crc32 = Crc32.Parse(seed[..8]),
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = Guid.NewGuid()
            };
            context.RomFiles.Add(rom);
            await context.SaveChangesAsync();
            result.Add((rom.Id, fileName, size));
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> ListPublicIdsAsync(
        HttpClient client,
        IReadOnlyList<(int Id, string FileName, long Size)> seeded)
    {
        using var response = await client.GetAsync("/api/roms?limit=100");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var requestedByFileName = seeded.ToDictionary(item => item.FileName, StringComparer.Ordinal);
        JsonElement[] requestedItems = document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Where(item => requestedByFileName.ContainsKey(item.GetProperty("originalFilename").GetString()!))
            .ToArray();
        var idsByFileName = requestedItems
            .ToDictionary(
                item => item.GetProperty("originalFilename").GetString()!,
                item => item.GetProperty("id").GetString()!,
                StringComparer.Ordinal);
        foreach (JsonElement item in requestedItems)
        {
            string fileName = item.GetProperty("originalFilename").GetString()!;
            JsonElement size = item.GetProperty("size");
            size.ValueKind.ShouldBe(JsonValueKind.String);
            size.GetString().ShouldBe(requestedByFileName[fileName].Size.ToString());
        }

        return seeded.Select(item => idsByFileName[item.FileName]).ToArray();
    }

    private async Task AssertRomsExistAsync(IEnumerable<int> romIds, bool expected)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        foreach (int romId in romIds)
        {
            (await context.RomFiles.AsNoTracking().AnyAsync(rom => rom.Id == romId)).ShouldBe(expected);
        }
    }
}
