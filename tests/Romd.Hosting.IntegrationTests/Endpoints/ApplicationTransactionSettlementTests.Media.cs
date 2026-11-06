using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Commands;
using Romd.Contracts.Management.Models;
using Romd.Domain.Hashing;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ApplicationTransactionSettlementTests
{
    [Theory]
    [InlineData(false, "none")]
    [InlineData(false, "before")]
    [InlineData(false, "after")]
    [InlineData(true, "none")]
    [InlineData(true, "before")]
    [InlineData(true, "after")]
    public async Task RomDeletion_ActualMappedOwner_KeepsDurableDeletionAndOutboxTogether(bool purge, string commitFault)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        int romId = await SeedSettlementRomAsync(host);
        int lastEventId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            lastEventId = await db.AdminRealtimeOutboxEvents.Select(row => (int?)row.Id).MaxAsync() ?? 0;
        }
        host.Faults.CommitFault = commitFault;
        using var client = host.CreateAuthenticatedClient();

        using var response = purge
            ? await client.PostAsync("/api/roms/unidentified/purge", null)
            : await client.DeleteAsync($"/api/roms/{IdCoder.Encode(romId)}");

        if (commitFault == "none")
        {
            response.StatusCode.ShouldBe(purge ? HttpStatusCode.OK : HttpStatusCode.NoContent,
                await response.Content.ReadAsStringAsync());
            if (purge)
            {
                var result = (await response.Content.ReadFromJsonAsync<PurgeUnidentifiedResponse>()).ShouldNotBeNull();
                result.DeletedCount.ShouldBe(1);
                result.ReclaimedFileCount.ShouldBe(0); // The new physical file remains protected by the one-hour age bound.
            }
        }
        else
        {
            using var problem = await ProblemEnvelope.ReadAsync(response, 500,
                purge ? "General.Unhandled" : "Library.StorageFailed");
            string body = await response.Content.ReadAsStringAsync();
            body.ShouldNotContain(host.Faults.Primary.Message);
            body.ShouldNotContain(host.Faults.Cleanup.Message);
        }
        response.Headers.ETag.ShouldBeNull();
        host.Faults.Disposals.ShouldBeGreaterThanOrEqualTo(1);
        await using var verifyScope = host.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await verify.RomFiles.AnyAsync(row => row.Id == romId)).ShouldBe(commitFault == "before");
        var events = await verify.AdminRealtimeOutboxEvents.Where(row => row.Id > lastEventId)
            .Select(row => row.EventType).ToListAsync();
        if (commitFault == "before")
            events.ShouldBeEmpty();
        else
        {
            events.ShouldContain(AdminRealtimeEventTypes.StorageStatsChanged);
            events.ShouldContain(AdminRealtimeEventTypes.CoverageStatsChanged);
            events.ShouldContain(AdminRealtimeEventTypes.HealthStatsChanged);
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("before")]
    [InlineData("after")]
    public async Task TitleUpload_ActualMultipartOwner_KeepsMediaFileRevisionAndOutboxTogether(string commitFault)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        int titleId;
        Guid originalRevision;
        int originalFiles, lastEventId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var title = new TitleEntity
            {
                PlatformId = await db.Platforms.OrderBy(row => row.Id).Select(row => row.Id).FirstAsync(),
                Name = "Settlement media title",
                NormalizedName = "settlementmediatitle",
                EnrichmentStatus = "None"
            };
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            titleId = title.Id;
            originalRevision = title.Revision;
            originalFiles = await db.Files.CountAsync();
            lastEventId = await db.AdminRealtimeOutboxEvents.Select(row => (int?)row.Id).MaxAsync() ?? 0;
        }
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6L8AAAAASUVORK5CYII=");
        host.Faults.CommitFault = commitFault;
        using var client = host.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/titles/{IdCoder.Encode(titleId)}/media?type=cover")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "settlement.png" } }
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{originalRevision}\"").ShouldBeTrue();

        using var response = await client.SendAsync(request);

        if (commitFault == "none")
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<TitleMediaRef>()).ShouldNotBeNull();
            result.Type.ShouldBe("Cover");
            result.SourceId.ShouldBe("user");
            response.Headers.Location.ShouldNotBeNull().OriginalString
                .ShouldBe($"/api/titles/{IdCoder.Encode(titleId)}/media/{result.Id}");
            response.Headers.ETag.ShouldNotBeNull();
        }
        else
        {
            using var problem = await ProblemEnvelope.ReadAsync(response, 500, "General.Unhandled");
            string body = await response.Content.ReadAsStringAsync();
            body.ShouldNotContain(host.Faults.Primary.Message);
            body.ShouldNotContain(host.Faults.Cleanup.Message);
            response.Headers.ETag.ShouldBeNull();
        }
        host.Faults.Disposals.ShouldBe(1);
        await using var verifyScope = host.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var media = await verify.TitleMedia.SingleOrDefaultAsync(row => row.TitleId == titleId);
        var revision = await verify.Titles.Where(row => row.Id == titleId).Select(row => row.Revision).SingleAsync();
        var events = await verify.AdminRealtimeOutboxEvents.Where(row => row.Id > lastEventId)
            .Select(row => row.EventType).ToListAsync();
        if (commitFault == "before")
        {
            media.ShouldBeNull();
            revision.ShouldBe(originalRevision);
            (await verify.Files.CountAsync()).ShouldBe(originalFiles);
            events.ShouldBeEmpty();
        }
        else
        {
            media.ShouldNotBeNull().SourceId.ShouldBe("user");
            revision.ShouldNotBe(originalRevision);
            (await verify.Files.CountAsync()).ShouldBe(originalFiles + 1);
            events.ShouldBe([AdminRealtimeEventTypes.StorageStatsChanged]);
            if (commitFault == "none")
                response.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe($"\"{revision}\"");
            var storage = verifyScope.ServiceProvider.GetRequiredService<IFileStorageService>();
            await using var stored = (await storage.RetrieveByIdAsync(media.FileId, default)).ShouldNotBeNull();
            using var copy = new MemoryStream();
            await stored.CopyToAsync(copy);
            copy.ToArray().ShouldBe(png);
        }
    }

    private static async Task<int> SeedSettlementRomAsync(SettlementFixture host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        byte[] bytes = [1, 3, 5, 7, 9];
        await using var input = new MemoryStream(bytes, writable: false);
        var stored = await storage.StageAndCommitAsync(db, input);
        var rom = new OriginalRomFixture
        {
            OriginalFilename = "settlement.rom",
            FileId = stored.File.Id,
            Sha1 = Sha1.FromBytes(System.Security.Cryptography.SHA1.HashData(bytes)),
            Md5 = Md5.FromBytes(System.Security.Cryptography.MD5.HashData(bytes)),
            Crc32 = Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.StageOriginalRom(rom);
        await db.PublishOriginalRomsAsync();
        return rom.Id;
    }
}
