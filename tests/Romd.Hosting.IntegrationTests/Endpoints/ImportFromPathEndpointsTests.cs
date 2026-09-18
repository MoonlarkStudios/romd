using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Management.Models;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ImportFromPathEndpointsTests(IntegrationTestFixture fixture)
{
    private string CreateSourceTree(out string topFile, out string nestedFile)
    {
        string sourceRoot = Path.Combine(fixture.ImportRootPath, $"roms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "nested"));
        topFile = Path.Combine(sourceRoot, "top.rom");
        nestedFile = Path.Combine(sourceRoot, "nested", "deep.rom");
        File.WriteAllBytes(topFile, [0x10, 0x20, 0x30, 0x40]);
        File.WriteAllBytes(nestedFile, [0x50, 0x60, 0x70, 0x80]);
        return sourceRoot;
    }

    private static async Task<(HttpStatusCode Status, string? ErrorCode)> PostImportAsync(
        HttpClient client,
        object body)
    {
        using var response = await client.PostAsJsonAsync("/api/upload/from-path", body);
        string content = await response.Content.ReadAsStringAsync();
        string? errorCode = null;
        if (!response.IsSuccessStatusCode && content.Length > 0)
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("errorCode", out var codeProperty))
            {
                errorCode = codeProperty.GetString();
            }
        }

        return (response.StatusCode, errorCode);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Condition not met within 10 seconds: {description}");
    }

    [Fact]
    public async Task ImportFromPath_CopyMode_Returns202AndCompletesRetainingSources()
    {
        string sourceRoot = CreateSourceTree(out string topFile, out string nestedFile);
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/api/upload/from-path",
            new { path = sourceRoot, allowUnidentified = true });
        var accepted = await response.Content.ReadFromJsonAsync<UploadAccepted>();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location.ShouldNotBeNull();
        accepted.ShouldNotBeNull();

        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var persisted = await db.Set<UploadJobEntity>()
            .AsNoTracking()
            .SingleAsync(job => job.Id == accepted.JobId);
        persisted.ImportSourcePath.ShouldBe(sourceRoot);
        persisted.ImportMove.ShouldBeFalse();
        persisted.SourceFilename.ShouldBe($"{Path.GetFileName(sourceRoot)}/");
        persisted.RomsDiscovered.ShouldBe(2);

        // Copy mode: source originals are never touched.
        File.Exists(topFile).ShouldBeTrue();
        File.Exists(nestedFile).ShouldBeTrue();
    }

    [Fact]
    public async Task ImportFromPath_MoveMode_DeletesSourcesOnlyAfterCompletion()
    {
        string sourceRoot = CreateSourceTree(out string topFile, out string nestedFile);
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/api/upload/from-path",
            new { path = sourceRoot, move = true, allowUnidentified = true });
        var accepted = await response.Content.ReadFromJsonAsync<UploadAccepted>();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        accepted.ShouldNotBeNull();

        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());

        // The terminal cleanup hook runs just after the completion checkpoint becomes visible.
        string manifestPath = Path.Combine(
            fixture.DataDirectoryPath, "temp", "jobs", $"{accepted.JobId:N}.import.json");
        await WaitForConditionAsync(
            () => !File.Exists(topFile) && !File.Exists(nestedFile) && !File.Exists(manifestPath),
            "move-mode sources and manifest deleted after successful completion");
    }

    [Fact]
    public async Task ImportFromPath_MoveMode_ArchiveNameCollidingWithStagedDirectory_IngestsStagedBytesBeforeDeletingOriginal()
    {
        // Reviewer's F1 reproduction: the source stages game.zip (containing foo.rom with bytes A)
        // AND game/foo.rom (bytes B). Extraction must not overwrite the staged file — the staged
        // foo.rom is processed as itself (bytes B) and the archive's foo.rom from a fresh
        // directory, so the move-mode deletion of the original is authorized by the bytes that
        // actually reached storage.
        byte[] archiveRomBytes = [0xA0, 0xA1, 0xA2, 0xA3];
        byte[] stagedRomBytes = [0xB0, 0xB1, 0xB2, 0xB3];
        string sourceRoot = Path.Combine(fixture.ImportRootPath, $"roms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "game"));
        string stagedFile = Path.Combine(sourceRoot, "game", "foo.rom");
        await File.WriteAllBytesAsync(stagedFile, stagedRomBytes);
        await using (var zipStream = File.Create(Path.Combine(sourceRoot, "game.zip")))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            await using var entryStream = archive.CreateEntry("foo.rom").Open();
            entryStream.Write(archiveRomBytes);
        }

        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync(
            "/api/upload/from-path",
            new { path = sourceRoot, move = true, allowUnidentified = true });
        var accepted = await response.Content.ReadFromJsonAsync<UploadAccepted>();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        accepted.ShouldNotBeNull();
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.JobId.ToString());

        var stagedSha1 = Sha1.FromBytes(SHA1.HashData(stagedRomBytes));
        var archiveSha1 = Sha1.FromBytes(SHA1.HashData(archiveRomBytes));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var storedHashes = await db.Set<RomFileEntity>().AsNoTracking().Select(r => r.Sha1).ToListAsync();
        storedHashes.ShouldContain(stagedSha1);
        storedHashes.ShouldContain(archiveSha1);

        // The original is deleted only because its own bytes were durably stored.
        string manifestPath = Path.Combine(
            fixture.DataDirectoryPath, "temp", "jobs", $"{accepted.JobId:N}.import.json");
        await WaitForConditionAsync(
            () => !File.Exists(stagedFile) && !File.Exists(manifestPath),
            "staged original and manifest deleted after its bytes were ingested");
    }

    [Fact]
    public async Task ImportFromPath_NonManager_Returns403()
    {
        using var client = fixture.CreateClient()
            .WithTestUser(Guid.NewGuid(), "contributor@localhost", [RomdRoleType.Contributor]);

        var (status, _) = await PostImportAsync(client, new { path = fixture.ImportRootPath });

        status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ImportFromPath_RelativePath_Returns400PathNotAbsolute()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var (status, errorCode) = await PostImportAsync(
            client, new { path = Path.Combine("relative", "roms") });

        status.ShouldBe(HttpStatusCode.BadRequest);
        errorCode.ShouldBe("Import.PathNotAbsolute");
    }

    [Fact]
    public async Task ImportFromPath_EscapeViaDotDot_Returns400PathNotAllowed()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var (status, errorCode) = await PostImportAsync(
            client, new { path = Path.Combine(fixture.ImportRootPath, "..", "escaped") });

        status.ShouldBe(HttpStatusCode.BadRequest);
        errorCode.ShouldBe("Import.PathNotAllowed");
    }

    [Fact]
    public async Task ImportFromPath_UnlistedRoot_Returns400PathNotAllowed()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var (status, errorCode) = await PostImportAsync(
            client, new { path = Path.Combine(Path.GetTempPath(), "romd-unlisted-root") });

        status.ShouldBe(HttpStatusCode.BadRequest);
        errorCode.ShouldBe("Import.PathNotAllowed");
    }
}
