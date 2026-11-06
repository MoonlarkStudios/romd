using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Management.Models;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class SevenZipUploadEndpointsTests(IntegrationTestFixture fixture)
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SevenZip", name);

    // Extractor tests cover codec variants; these cases exercise upload and nested-archive routing.
    [Theory]
    [InlineData("solid.7z", 3)]
    [InlineData("zip-in-7z.7z", 1)]
    [InlineData("7z-in-zip.zip", 3)]
    public async Task Upload_SevenZipAndMixedNestedArchives_PublishesMemberOutcomes(string name, int count)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(File.ReadAllBytes(Fixture(name))), "file", "pack" + Path.GetExtension(name));
        using var response = await client.PostAsync("/api/upload?allowUnidentified=true", content);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var job = await db.Set<UploadJobEntity>().SingleAsync(j => j.Id == accepted.JobId);
        job.Phase.ShouldBe("Completed");
        job.RomsDiscovered.ShouldBe(count);
        var outcomes = await db.JobItems.Where(i => i.JobId == accepted.JobId).ToListAsync();
        outcomes.Count.ShouldBe(count);
        outcomes.ShouldAllBe(i => i.Outcome == (int)JobItemOutcome.Ingested || i.Outcome == (int)JobItemOutcome.Deduplicated);
        outcomes.ShouldAllBe(i => i.FileName.StartsWith("pack/"));
    }

    [Theory]
    [InlineData(".zip", true)]
    [InlineData(".7z", true)]
    [InlineData(".7z", false)]
    public async Task ImportFromPath_ArchiveCleanup_MatchesZipDurableOutcomeRules(string extension, bool move)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture.ImportRootPath, $"sevenzip-{Guid.NewGuid():N}")).FullName;
        string archivePath = Path.Combine(root, "pack" + extension);
        if (extension == ".7z")
            File.Copy(Fixture("copy-seed.7z"), archivePath);
        else
        {
            using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            using var entry = zip.CreateEntry("a.rom").Open();
            entry.Write("alpha"u8);
        }
        string sibling = Path.Combine(root, "sibling.rom");
        File.WriteAllBytes(sibling, [10, 20, 30]);
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/upload/from-path", new { path = root, move, allowUnidentified = true });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await db.Set<UploadJobEntity>().SingleAsync(j => j.Id == accepted.JobId)).Phase.ShouldBe("Completed");
        var outcomes = await db.JobItems.Where(i => i.JobId == accepted.JobId).ToListAsync();
        outcomes.ShouldContain(i => i.FileName == "pack/a.rom" && (i.Outcome == (int)JobItemOutcome.Ingested || i.Outcome == (int)JobItemOutcome.Deduplicated));
        if (move)
        {
            string manifest = Path.Combine(fixture.DataDirectoryPath, "temp", "jobs", $"{accepted.JobId:N}.import.json");
            await WaitForAsync(() => !File.Exists(sibling) && !File.Exists(manifest));
        }
        else
            File.Exists(sibling).ShouldBeTrue();
        // Existing ZIP policy conservatively retains archive originals: extracted-member
        // outcomes do not prove durable storage of the source archive itself.
        File.Exists(archivePath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("rar")]
    [InlineData("gzip")]
    public async Task ImportFromPath_UnsupportedArchive_RecordsFailureAndRetainsMoveSource(string format)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture.ImportRootPath, $"unsupported-{Guid.NewGuid():N}")).FullName;
        string filename = format == "rar" ? "unsupported.rar" : "unsupported.gz";
        string source = Path.Combine(root, filename);
        File.WriteAllBytes(source, format == "rar" ? [0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0] : [0x1f, 0x8b, 0, 0]);
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/upload/from-path", new { path = root, move = true, allowUnidentified = true });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await db.Set<UploadJobEntity>().SingleAsync(j => j.Id == accepted.JobId)).Phase.ShouldBe("CompletedWithErrors");
        var item = await db.JobItems.SingleAsync(i => i.JobId == accepted.JobId);
        item.FileName.ShouldBe(filename);
        item.Outcome.ShouldBe((int)JobItemOutcome.Failed);
        item.Error.ShouldNotBeNull().ShouldContain("no supported extractor");
        File.Exists(source).ShouldBeTrue();
    }

    [Fact]
    public async Task ImportFromPath_DamagedUnsupportedArchives_FailsEveryItemAndRetainsMoveSources()
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture.ImportRootPath, $"damaged-archives-{Guid.NewGuid():N}")).FullName;
        var inputs = new Dictionary<string, byte[]>
        {
            ["truncated.gz"] = [0x1F, 0x8B],
            ["truncated.rar"] = [0x52, 0x61, 0x72, 0x21, 0x1A],
            ["invalid.gz"] = "not an archive"u8.ToArray(),
            ["invalid.rar"] = "not an archive"u8.ToArray(),
            ["empty.Gz"] = [],
            ["empty.RaR"] = []
        };
        foreach (var (name, bytes) in inputs)
            File.WriteAllBytes(Path.Combine(root, name), bytes);

        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/upload/from-path", new { path = root, move = true, allowUnidentified = true });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var job = await db.Set<UploadJobEntity>().SingleAsync(j => j.Id == accepted.JobId);
        job.Phase.ShouldBe("CompletedWithErrors");
        job.ImportMove.ShouldBeTrue();
        job.RomsDiscovered.ShouldBe(inputs.Count);
        job.RomsProcessed.ShouldBe(inputs.Count);
        job.RomsIngested.ShouldBe(0);
        job.RomsDeduplicated.ShouldBe(0);
        var items = await db.JobItems.Where(i => i.JobId == accepted.JobId).ToListAsync();
        items.Count.ShouldBe(inputs.Count);
        foreach (var (name, bytes) in inputs)
        {
            var item = items.Single(i => i.FileName == name);
            item.Outcome.ShouldBe((int)JobItemOutcome.Failed);
            item.Error.ShouldNotBeNull().ShouldContain("no supported extractor");
            File.Exists(Path.Combine(root, name)).ShouldBeTrue();
            File.ReadAllBytes(Path.Combine(root, name)).ShouldBe(bytes);
        }
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (int i = 0; i < 200; i++)
        {
            if (done())
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Move-mode cleanup did not finish.");
    }
}
