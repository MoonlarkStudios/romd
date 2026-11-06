using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Source;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class OriginalDiscDescriptorIntakeTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upload_CueBeforeOrAfterTrack_PreservesBytesAndReconcilesExactSiblingAfterJournal(bool cueFirst)
    {
        string name = "descriptor-" + Guid.NewGuid().ToString("N");
        byte[] track = Encoding.UTF8.GetBytes(name + " original track");
        byte[] cue = Encoding.UTF8.GetBytes("REM preserve CRLF and original spelling\r\nFILE \"track.bin\" BINARY\r\n TRACK 01 MODE2/2352\r\n");
        byte[] dat = Dat(name, track);
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        {
            Write(zip, "catalog.dat", dat);
            foreach (var entry in cueFirst ? new[] { ("game.cue", cue), ("track.bin", track) } : new[] { ("track.bin", track), ("game.cue", cue) })
                Write(zip, entry.Item1, entry.Item2);
        }
        using var client = fixture.CreateAuthenticatedClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(zipBytes.ToArray()), "file", name + ".zip");
        var response = await client.PostAsync("/api/upload?systemKey=psx&allowUnidentified=false&maxParallelRoms=2", form);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var job = await db.Set<UploadJobEntity>().SingleAsync(x => x.Id == accepted.JobId);
        job.Phase.ShouldBe("Completed");
        var layout = await db.OriginalDiscLayouts.Include(x => x.Files).SingleAsync(x => x.SourceJobId == accepted.JobId);
        layout.State.ShouldBe("pending");
        layout.Files.ShouldHaveSingleItem().Path.ShouldBe(name + "/track.bin");
        var outcome = await db.JobItems.SingleAsync(x => x.JobId == accepted.JobId && x.FileName == name + "/game.cue");
        outcome.Outcome.ShouldBe((int)JobItemOutcome.Ingested);
        outcome.RomFileId.ShouldBeNull();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        await using var original = await storage.RetrieveByIdAsync(layout.DescriptorFileId, default);
        original.ShouldNotBeNull();
        using var storedBytes = new MemoryStream();
        await original.CopyToAsync(storedBytes);
        storedBytes.ToArray().ShouldBe(cue);
        var descriptorHash = Sha1.FromBytes(SHA1.HashData(cue));
        (await db.RomFiles.AnyAsync(x => x.Sha1 == descriptorHash)).ShouldBeFalse();
        // A fresh scope simulates replay after an acknowledgement/restart boundary.
        await using var replayScope = fixture.Services.CreateAsyncScope();
        var intake = replayScope.ServiceProvider.GetRequiredService<IOriginalDiscDescriptorIntake>();
        await using var replayStream = new MemoryStream(cue);
        var replay = await intake.IntakeAsync(replayStream, accepted.JobId, name + "/game.cue", default);
        replay.Id.ShouldBe(outcome.Id);
        await intake.ReconcileAsync(accepted.JobId, default);
        (await db.OriginalDiscLayouts.CountAsync(x => x.SourceJobId == accepted.JobId)).ShouldBe(1);
        (await db.OriginalDiscLayoutFiles.CountAsync(x => x.OriginalDiscLayoutId == layout.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task DescriptorOnlyRepair_MoveMode_DeletesSourceOnlyAfterDurableDescriptorAndJournal()
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture.ImportRootPath, "descriptor-repair-" + Guid.NewGuid().ToString("N"))).FullName;
        string source = Path.Combine(root, "repair.cue");
        byte[] cue = Encoding.UTF8.GetBytes("FILE \"missing.bin\" BINARY\n TRACK 01 MODE2/2352\n");
        await File.WriteAllBytesAsync(source, cue);
        using var client = fixture.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/upload/from-path", new { path = root, move = true, allowUnidentified = false });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var layout = await db.OriginalDiscLayouts.SingleAsync(x => x.SourceJobId == accepted.JobId);
        layout.State.ShouldBe("pending");
        (await db.JobItems.SingleAsync(x => x.JobId == accepted.JobId)).Outcome.ShouldBe((int)JobItemOutcome.Ingested);
        await using var original = await scope.ServiceProvider.GetRequiredService<IFileStorageService>().RetrieveByIdAsync(layout.DescriptorFileId, default);
        original.ShouldNotBeNull();
        using var bytes = new MemoryStream();
        await original.CopyToAsync(bytes);
        bytes.ToArray().ShouldBe(cue);
        for (int i = 0; i < 100 && File.Exists(source); i++)
            await Task.Delay(50);
        File.Exists(source).ShouldBeFalse();
    }

    [Theory]
    [InlineData("unmatched", false)]
    [InlineData("unmatched", true)]
    [InlineData("catalog-matched-untracked", false)]
    [InlineData("catalog-matched-untracked", true)]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    public async Task Import_TrackedOnlyCueWithoutTrackedOwner_RejectsWithoutAttachmentAndPreservesSource(string kind, bool move)
    {
        string name = "tracked-only-cue-" + Guid.NewGuid().ToString("N");
        string root = Directory.CreateDirectory(Path.Combine(fixture.ImportRootPath, name)).FullName;
        string source = Path.Combine(root, "original.cue");
        byte[] cue = kind == "empty" ? [] : Encoding.UTF8.GetBytes($"REM {name}\nFILE track.bin BINARY\n TRACK 01 MODE2/2352\n");
        await File.WriteAllBytesAsync(source, cue);
        using var client = fixture.CreateAuthenticatedClient();
        if (kind == "catalog-matched-untracked")
        {
            using var catalog = new MultipartFormDataContent();
            catalog.Add(new ByteArrayContent(Dat(name, cue)), "file", name + ".dat");
            using var imported = await client.PostAsync("/api/upload/dat?systemKey=psx", catalog);
            imported.StatusCode.ShouldBe(HttpStatusCode.Accepted, await imported.Content.ReadAsStringAsync());
            var catalogJob = (await imported.Content.ReadFromJsonAsync<JobReference>())!;
            await TestHelpers.WaitForJobCompletionAsync(client, catalogJob.JobId.ToString());
        }

        var sha1 = Sha1.FromBytes(SHA1.HashData(cue));
        var sha256 = Sha256.FromBytes(SHA256.HashData(cue));
        int originalFileCount;
        int originalRomCount;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            // The request has a tracked title, but the descriptor belongs to none of them.
            var unrelated = new TitleEntity
            {
                PlatformId = await db.Platforms.Where(x => x.CanonicalKey == "psx").Select(x => x.Id).SingleAsync(),
                Name = name + " unrelated",
                NormalizedName = name + " unrelated",
                EnrichmentStatus = "None",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Titles.Add(unrelated);
            await db.SaveChangesAsync();
            db.TrackedTitles.Add(new() { TitleId = unrelated.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var match = await scope.ServiceProvider.GetRequiredService<IRomCatalogMatchReader>().ReadAsync(sha1);
            match.HasCatalogMatch.ShouldBe(kind == "catalog-matched-untracked");
            (await db.TrackedTitles.AnyAsync(x => match.TitleIds.Contains(x.TitleId))).ShouldBeFalse();
            originalFileCount = await db.Files.CountAsync(x => x.Sha256 == sha256);
            originalRomCount = await db.RomFiles.CountAsync(x => x.Sha1 == sha1);
        }

        using var response = await client.PostAsJsonAsync("/api/upload/from-path",
            new { path = root, move, trackedOnly = true, allowUnidentified = false });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var readScope = fixture.Services.CreateAsyncScope();
        var read = readScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await read.Set<UploadJobEntity>().SingleAsync(x => x.Id == accepted.JobId)).Phase.ShouldBe("Completed");
        var outcome = await read.JobItems.SingleAsync(x => x.JobId == accepted.JobId);
        outcome.Outcome.ShouldBe((int)JobItemOutcome.Rejected);
        outcome.Error.ShouldBe(CatalogErrors.UntrackedRom().Description);
        outcome.RomFileId.ShouldBeNull();
        (await read.OriginalDiscLayouts.AnyAsync(x => x.SourceJobId == accepted.JobId)).ShouldBeFalse();
        (await read.Files.CountAsync(x => x.Sha256 == sha256)).ShouldBe(originalFileCount);
        (await read.RomFiles.CountAsync(x => x.Sha1 == sha1)).ShouldBe(originalRomCount);
        File.Exists(source).ShouldBeTrue();
        (await File.ReadAllBytesAsync(source)).ShouldBe(cue);
    }

    [Theory]
    [InlineData("empty", "UnsupportedDescriptor")]
    [InlineData("non-utf8", "UnsupportedEncoding")]
    [InlineData("unsafe", "UnsafeDependencyPath")]
    public async Task Import_MalformedOriginalCue_RetainsExactBytesWithVisibleRepairIssue(string kind, string issue)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture.ImportRootPath, "invalid-descriptor-" + Guid.NewGuid().ToString("N"))).FullName;
        byte[] cue = kind switch { "empty" => [], "non-utf8" => [0xff, 0xfe], _ => Encoding.UTF8.GetBytes("FILE \"../track.bin\" BINARY\n") };
        await File.WriteAllBytesAsync(Path.Combine(root, "invalid.cue"), cue);
        using var client = fixture.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/upload/from-path", new { path = root, move = false, allowUnidentified = false });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var layout = await db.OriginalDiscLayouts.SingleAsync(x => x.SourceJobId == accepted.JobId);
        layout.State.ShouldBe("pending");
        layout.Issue.ShouldBe(issue);
        (await db.JobItems.SingleAsync(x => x.JobId == accepted.JobId)).Outcome.ShouldBe((int)JobItemOutcome.Ingested);
        await using var original = await scope.ServiceProvider.GetRequiredService<IFileStorageService>().RetrieveByIdAsync(layout.DescriptorFileId, default);
        original.ShouldNotBeNull();
        using var bytes = new MemoryStream();
        await original.CopyToAsync(bytes);
        bytes.ToArray().ShouldBe(cue);
    }
    [Fact]
    public async Task Intake_RepositoryFailureAfterPublication_RollsBackFileLedgerAndOutcomeTogether()
    {
        // Real ledger/unit-of-work; injected association failure exercises the publication rollback seam.
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<RomdDbContext>();
        var descriptors = Substitute.For<IOriginalDiscDescriptorRepository>();
        descriptors.RecordStagedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OriginalCueDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("association write failed")));
        var intake = new OriginalDiscDescriptorIntake(descriptors, services.GetRequiredService<IFileStorageService>(), services.GetRequiredService<IJobItemRepository>(), services.GetRequiredService<IUnitOfWork>(), services.GetRequiredService<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), clock: TimeProvider.System);
        byte[] cue = Encoding.UTF8.GetBytes("REM " + Guid.NewGuid() + "\nFILE track.bin BINARY\n");
        var hash = Sha256.FromBytes(SHA256.HashData(cue));
        Guid jobId = Guid.NewGuid();
        await using var stream = new MemoryStream(cue);
        await Should.ThrowAsync<InvalidOperationException>(() => intake.IntakeAsync(stream, jobId, "repair.cue", default));
        (await db.Files.AnyAsync(x => x.Sha256 == hash)).ShouldBeFalse();
        (await db.JobItems.AnyAsync(x => x.JobId == jobId)).ShouldBeFalse();
        (await db.OriginalDiscLayouts.AnyAsync(x => x.SourceJobId == jobId)).ShouldBeFalse();
    }
    private static byte[] Dat(string name, byte[] track) => Encoding.UTF8.GetBytes($"<datafile><header><name>{name}</name><description>{name}</description><version>1</version></header><game name=\"{name} (USA)\"><rom name=\"track.bin\" size=\"{track.Length}\" crc=\"{System.IO.Hashing.Crc32.HashToUInt32(track):x8}\" md5=\"{Convert.ToHexStringLower(MD5.HashData(track))}\" sha1=\"{Convert.ToHexStringLower(SHA1.HashData(track))}\"/></game></datafile>");
    private static void Write(ZipArchive zip, string path, byte[] bytes) { using var entry = zip.CreateEntry(path).Open(); entry.Write(bytes); }
}
