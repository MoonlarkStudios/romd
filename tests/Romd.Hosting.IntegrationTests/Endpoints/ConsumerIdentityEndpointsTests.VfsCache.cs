using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Mount.Cli;
using Shouldly;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Ids;
using Romd.Persistence.Entities;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    private static async Task ProbeMinimumFreeSpaceAsync(string mountedPath, string cachePath,
        VfsProbeContent source, VfsScratchVolume volume, CancellationToken cancellationToken)
    {
        await using (var file = File.OpenRead(mountedPath))
            (await SHA256.HashDataAsync(file, cancellationToken)).ShouldBe(source.Sha256);
        string cachedFile = Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)
            .Single(path => new FileInfo(path).Length == source.Length);
        long beforePressure = volume.AvailableBytes;
        beforePressure.ShouldBeGreaterThan(32L * 1024 * 1024);
        try
        {
            long filler = volume.ApplyPressure(30L * 1024 * 1024);
            long pressured = volume.AvailableBytes;
            pressured.ShouldBeLessThan(32L * 1024 * 1024);
            long started = Stopwatch.GetTimestamp();
            while (File.Exists(cachedFile) && Stopwatch.GetElapsedTime(started).TotalSeconds < 125)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            File.Exists(cachedFile).ShouldBeFalse("The free-space target must evict this closed 4 MiB cache despite its 1 GiB size target.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                probe = "minimum-free-space",
                sourceBytes = source.Length,
                sizeTargetMiB = 1024,
                freeTargetMiB = 32,
                beforePressure,
                filler,
                pressured,
                afterEviction = volume.AvailableBytes,
                evictionSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                fullHashMatched = true
            }));
        }
        finally { volume.ReleasePressure(); }
        // Verify reads remain correct after pressure is removed. This can use the
        // kernel cache; it is not proof of another network download.
        await using var reread = File.OpenRead(mountedPath);
        (await SHA256.HashDataAsync(reread, cancellationToken)).ShouldBe(source.Sha256);
    }

    private sealed partial class ConsumerHostFixture
    {
        public async Task<string[]> AddCacheProbeAliasesAsync(int library, string originalPath, int copies)
        {
            await using var db = CreateSetupDbContext();
            var original = await db.MaterializedLibraryReleases.AsNoTracking().SingleAsync(row => row.LibraryId == library);
            var content = await db.CatalogReleaseFiles.AsNoTracking().SingleAsync(row => row.CatalogReleaseId == original.CatalogReleaseId);
            var paths = new List<string> { originalPath };
            for (int i = 1; i <= copies; i++)
            {
                var release = new CatalogReleaseEntity
                {
                    CatalogTitleId = original.TitleId,
                    PlatformId = original.PlatformId,
                    Name = "Cache " + i,
                    Fingerprint = "vfs-cache-alias-" + i,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.CatalogReleases.Add(release);
                await db.SaveChangesAsync();
                db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
                {
                    CatalogReleaseId = release.Id,
                    FileFingerprint = content.FileFingerprint,
                    Name = content.Name,
                    Size = content.Size,
                    Sha1 = content.Sha1
                });
                db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
                {
                    LibraryId = library,
                    TitleId = original.TitleId,
                    PlatformId = original.PlatformId,
                    CatalogReleaseId = release.Id,
                    DatGameId = i + 1,
                    DatFileId = i + 1,
                    IsOwned = true,
                    IsExposed = true,
                    IsComplete = true,
                    IsPlayable = true
                });
                await db.SaveChangesAsync();
                paths.Add(originalPath[..originalPath.IndexOf('/')] + "/" + IdCoder.Encode(release.Id) + " " + release.Name + "/" + content.Name);
            }
            return paths.ToArray();
        }
    }

    private static async Task ProbeConcurrentCacheAsync(string[] mountedPaths, string cachePath,
        VfsProbeContent source, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        var rounds = new List<object>();
        for (int round = 0; round < mountedPaths.Length / 2; round++)
        {
            long started = Stopwatch.GetTimestamp();
            // Both local handles remain open until both independently verified hashes finish.
            await using (var first = File.OpenRead(mountedPaths[round * 2]))
            await using (var second = File.OpenRead(mountedPaths[round * 2 + 1]))
            {
                var hashes = await Task.WhenAll(SHA256.HashDataAsync(first, cancellationToken).AsTask(),
                    SHA256.HashDataAsync(second, cancellationToken).AsTask());
                foreach (var hash in hashes)
                    hash.ShouldBe(source.Sha256);
            }
            double hashSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            long closed = Stopwatch.GetTimestamp();
            var samples = new List<object>();
            long remainingBytes;
            do
            {
                remainingBytes = 0;
                int count = 0;
                foreach (string file in Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var allocation = MacFileAllocation.Read(file);
                        if (allocation.Logical != source.Length)
                            continue;
                        remainingBytes += allocation.Allocated;
                        count++;
                    }
                    catch (IOException) when (!File.Exists(file)) { } // The cleaner can remove a sampled file.
                }
                samples.Add(new
                {
                    secondsAfterClose = Stopwatch.GetElapsedTime(closed).TotalSeconds,
                    cachedFiles = count,
                    allocatedBytes = remainingBytes
                });
                if (count == 0 || Stopwatch.GetElapsedTime(closed).TotalSeconds >= 125)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            } while (true);
            rounds.Add(new
            {
                round,
                concurrentFiles = 2,
                fullHashesMatched = true,
                hashSeconds,
                observationSeconds = Stopwatch.GetElapsedTime(closed).TotalSeconds,
                remainingBytes,
                samples
            });
            // Bound probe disk usage if cleanup is not observed; report that outcome.
            if (remainingBytes > 0)
                break;
        }
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("ROMD_VFS_CACHE_REPORT")!, JsonSerializer.Serialize(new
        {
            recordedAt = DateTimeOffset.UtcNow,
            sourceBytes = source.Length,
            sourceKind = source.Kind,
            backend = "nfs-development",
            cacheTargetBytes = 1024L * 1024 * 1024,
            plannedRounds = mountedPaths.Length / 2,
            completedRounds = rounds.Count,
            rounds,
            limitations = "Two rounds of two distinct catalog paths backed by one CAS object; paths have separate client cache entries. Full hashes verified concurrently through the supervised CLI mount. Warm server OS cache; client OS caches uncontrolled, with fresh paths each round. No forced eviction or remount. File allocation uses Darwin st_blocks, not reclaimable APFS space. Passing proves completed hashes/lifecycle, not eviction; inspect samples and completedRounds. This is not a hard quota, long soak, many-user or FSKit test. Cleanup continues after the report."
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static async Task ProbeCacheRetentionAsync(string mountedPath, string cachePath,
        VfsProbeContent source, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        long started = Stopwatch.GetTimestamp();
        string cachedFile;
        long allocatedWhileOpen;
        await using (var file = File.OpenRead(mountedPath))
        {
            (await SHA256.HashDataAsync(file, cancellationToken)).ShouldBe(source.Sha256);
            cachedFile = Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)
                .Single(path => new FileInfo(path).Length == source.Length);
            allocatedWhileOpen = MacFileAllocation.Read(cachedFile).Allocated;
        }
        double fullHashSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        long closed = Stopwatch.GetTimestamp();
        var samples = new List<object>();
        bool retained;
        do
        {
            retained = File.Exists(cachedFile);
            samples.Add(new { secondsAfterClose = Stopwatch.GetElapsedTime(closed).TotalSeconds, retained });
            if (!retained || Stopwatch.GetElapsedTime(closed).TotalSeconds >= 125)
                break;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        } while (true);
        // Characterization, not a passing eviction assertion: NFSv3 lacks a close
        // operation and can retain rclone handles after the local file closes.
        double observationSeconds = Stopwatch.GetElapsedTime(closed).TotalSeconds;
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("ROMD_VFS_CACHE_REPORT")!, JsonSerializer.Serialize(new
        {
            recordedAt = DateTimeOffset.UtcNow,
            sourceBytes = source.Length,
            sourceKind = source.Kind,
            backend = "nfs-development",
            cacheTargetBytes = 1024L * 1024 * 1024,
            fullHashMatched = true,
            fullHashSeconds,
            allocatedWhileOpen,
            observationSeconds,
            retainedAfterClose = retained,
            samples,
            limitations = "One file larger than the production 1 GiB cache target, full hash through the supervised CLI mount. Default rclone cache poll interval; no forced eviction, remount or cache purge. Cache existence is observed directly in the private test cache. File allocation is Darwin st_blocks, not reclaimable APFS space. Client/server OS caches uncontrolled. Passing this characterization does not assert eviction, a disk quota, long-soak stability or FSKit behavior. Cleanup and credential lifecycle continue after this report."
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    public sealed class MountCliCacheFactAttribute : FactAttribute
    {
        public MountCliCacheFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1" ||
                Environment.GetEnvironmentVariable("ROMD_VFS_MOUNT") != "1" ||
                !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")) ||
                !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_PROBE_FILE")) ||
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROMD_VFS_CACHE_REPORT")))
                Skip = "Requires native mount/keychain opt-ins, rclone, a source file larger than 1 GiB, and ROMD_VFS_CACHE_REPORT.";
        }
    }
}
