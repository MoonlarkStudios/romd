using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.PostgreSql.TestSupport;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Hashing;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Entities;
using Romd.Storage;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [RcloneFact]
    public Task Vfs_Rclone_RealNetworkDeviceLoginListingRangesAndRevocation() => RunVfsRcloneProbeAsync(false);

    [RcloneMountFact]
    public Task Vfs_RcloneMounted_FilesystemReadsMatchAuthorizedCas() => RunVfsRcloneProbeAsync(true);

    [NativeWebDavFact]
    public Task Vfs_NativeWebDav_ReadsMatchAuthorizedCas() => RunVfsRcloneProbeAsync(false, nativeWebDav: true);

    private static async Task RunVfsRcloneProbeAsync(bool mount, bool nativeWebDav = false)
    {
        var nativeAuthentication = nativeWebDav ? new NativeWebDavAuthenticationProbe() : null;
        int requestDelayMilliseconds = int.Parse(Environment.GetEnvironmentVariable("ROMD_VFS_REQUEST_DELAY_MS") ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        if (requestDelayMilliseconds is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(requestDelayMilliseconds));
        await using var fixture = await ConsumerHostFixture.CreateAsync(services =>
        {
            services.AddSingleton(new ConsumerVfsOptions { AllowDevelopmentBearer = true, Enabled = true });
            if (nativeAuthentication is not null)
                services.AddSingleton<IStartupFilter>(nativeAuthentication);
            if (requestDelayMilliseconds > 0)
                services.AddSingleton<IStartupFilter>(new VfsDelayStartupFilter(requestDelayMilliseconds));
        }, networkListener: true);
        int libraryId = await fixture.CreateLibraryAsync("Probe library");
        var user = await fixture.CreateConsumerUserAsync("probe", "probe@localhost", "Password123",
            user => user.LibraryId = libraryId);
        await using var source = await VfsProbeContent.CreateAsync();
        string relativePath = await fixture.SeedVfsProbeAsync(libraryId, source);
        using var client = fixture.Factory.CreateClient();
        using var device = await client.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
        {
            ["client_id"] = RomdOpenIddictClients.Mount,
            ["scope"] = "offline_access email profile roles"
        }));
        device.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codes = await ReadJsonObjectAsync(device);
        using var approval = await client.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(client, "/connect/verify", new Dictionary<string, string>
        {
            ["user_code"] = RequiredString(codes, "user_code"),
            ["login"] = "probe",
            ["password"] = "Password123"
        }));
        approval.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var token = await client.PostAsync("/connect/token", CreateDeviceTokenRequest(RequiredString(codes, "device_code"), RomdOpenIddictClients.Mount));
        token.StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = await ReadJsonObjectAsync(token);
        string accessToken = RequiredString(issued, "access_token");
        string url = new Uri(client.BaseAddress!, "/vfs/library/").AbsoluteUri;
        if (nativeAuthentication is not null)
            nativeAuthentication.BearerToken = accessToken;

        var listing = await RunRcloneAsync(url, accessToken, "lsjson", "romd:", "--recursive", "--files-only");
        listing.ExitCode.ShouldBe(0, listing.Error);
        using var entries = JsonDocument.Parse(listing.Bytes);
        entries.RootElement.EnumerateArray().Select(entry => entry.GetProperty("Path").GetString())
            .ShouldContain(relativePath);
        var full = await RunRcloneHashAsync(url, accessToken, relativePath);
        full.ExitCode.ShouldBe(0, full.Error);
        full.Hash.ShouldBe(source.Sha256);
        var range = await RunRcloneAsync(url, accessToken, "cat", "romd:" + relativePath,
            "--offset", "65530", "--count", "4096");
        range.ExitCode.ShouldBe(0, range.Error);
        range.Bytes.ShouldBe(source.ReadRange(65530, 4096));

        if (nativeAuthentication is not null)
            await NativeWebDavProbe.RunAsync(url, relativePath, source, nativeAuthentication);

        string? httpReportPath = Environment.GetEnvironmentVariable("ROMD_VFS_HTTP_REPORT");
        object? httpReport = null;
        if (!mount && !string.IsNullOrEmpty(httpReportPath))
        {
            var runs = new List<object>();
            for (int index = 0; index < 3; index++)
                runs.Add(await ProbeHttpReadsAsync(client, accessToken, relativePath, source));
            httpReport = new
            {
                recordedAt = DateTimeOffset.UtcNow,
                contentBytes = source.Length,
                requestDelayMilliseconds,
                workload = "Three batches of 128 seeded authenticated HTTP 4 KiB ranges; warm server OS cache; loopback",
                runs
            };
        }

        if (mount)
        {
            object direct = await ProbeHttpReadsAsync(client, accessToken, relativePath, source);
            await ProbeMountedReadsAsync(url, accessToken, relativePath, source, direct, requestDelayMilliseconds);
        }

        await fixture.RemoveVfsLibraryAccessAsync(user.Id);
        var denied = await RunRcloneAsync(url, accessToken, "cat", "romd:" + relativePath,
            "--offset", "0", "--count", "4096");
        denied.ExitCode.ShouldNotBe(0);
        denied.Bytes.ShouldBeEmpty();
        if (httpReport is not null)
            await File.WriteAllTextAsync(httpReportPath!, JsonSerializer.Serialize(httpReport, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<(int ExitCode, byte[] Bytes, string Error)> RunRcloneAsync(
        string url, string token, params string[] arguments)
    {
        var start = RcloneStart(url, token, arguments);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        Task read = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        await read;
        return (process.ExitCode, output.ToArray(), await error);
    }

    private static async Task<(int ExitCode, byte[] Hash, string Error)> RunRcloneHashAsync(string url, string token, string path)
    {
        using var process = Process.Start(RcloneStart(url, token, "cat", "romd:" + path))!;
        Task<byte[]> hash = SHA256.HashDataAsync(process.StandardOutput.BaseStream).AsTask();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await hash, await error);
    }

    private static ProcessStartInfo RcloneStart(string url, string token, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (string argument in new[] { "--config", "/dev/null", "--retries", "1", "--low-level-retries", "1", "--timeout", "20s" })
            start.ArgumentList.Add(argument);
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.Environment["RCLONE_CONFIG_ROMD_TYPE"] = "webdav";
        start.Environment["RCLONE_CONFIG_ROMD_VENDOR"] = "other";
        start.Environment["RCLONE_CONFIG_ROMD_URL"] = url;
        start.Environment["RCLONE_CONFIG_ROMD_BEARER_TOKEN"] = token;
        return start;
    }

    private static async Task<object> ProbeHttpReadsAsync(HttpClient client, string token, string relativePath, VfsProbeContent expected)
    {
        using var metrics = new VfsProbeMetrics();
        var timings = new List<double>();
        var random = new Random(1729);
        string path = "/vfs/library/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        for (int index = 0; index < 128; index++)
        {
            long offset = expected.NextOffset(random, 4096);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Range = new RangeHeaderValue(offset, offset + 4095);
            long started = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(request);
            byte[] actual = await response.Content.ReadAsByteArrayAsync();
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            actual.ShouldBe(expected.ReadRange(offset, 4096));
        }
        timings.Sort();
        return new
        {
            requests = timings.Count,
            p50Milliseconds = timings[63],
            p95Milliseconds = timings[121],
            p99Milliseconds = timings[126],
            server = metrics.Snapshot()
        };
    }

    private static async Task ProbeMountedReadsAsync(string url, string token, string relativePath, VfsProbeContent expected, object direct, int requestDelayMilliseconds,
        string? caCertificate = null)
    {
        using var metrics = new VfsProbeMetrics();
        string directory = Path.Combine(Path.GetTempPath(), "romd-vfs-mount-" + Guid.NewGuid().ToString("N"));
        string mountpoint = Path.Combine(directory, "mount");
        Directory.CreateDirectory(mountpoint);
        string? reportPath = Environment.GetEnvironmentVariable("ROMD_VFS_PROBE_REPORT");
        string? report = null;
        string cacheMode = Environment.GetEnvironmentVariable("ROMD_VFS_CACHE_MODE") ?? "off";
        if (cacheMode is not ("off" or "full"))
            throw new ArgumentException("ROMD_VFS_CACHE_MODE must be off or full.");
        int nfsReadAhead = int.Parse(Environment.GetEnvironmentVariable("ROMD_VFS_NFS_READAHEAD") ?? "16", System.Globalization.CultureInfo.InvariantCulture);
        if (nfsReadAhead is < 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(nfsReadAhead));
        int chunkLimitKib = int.Parse(Environment.GetEnvironmentVariable("ROMD_VFS_CHUNK_LIMIT_KIB") ?? "64", System.Globalization.CultureInfo.InvariantCulture);
        if (chunkLimitKib is < 64 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(chunkLimitKib));
        int bandwidthLimitKib = int.Parse(Environment.GetEnvironmentVariable("ROMD_VFS_BANDWIDTH_KIB") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (bandwidthLimitKib is < 0 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(bandwidthLimitKib));
        var start = RcloneStart(url, token,
            "nfsmount", "romd:", mountpoint, "--read-only", "--addr", "127.0.0.1:0",
            "--option", $"ro,soft,intr,nolocks,noquota,timeo=2,retrans=1,readahead={nfsReadAhead}",
            "--log-level", "DEBUG", "--log-file", Path.Combine(directory, "rclone.log"),
            "--vfs-cache-mode", cacheMode, "--vfs-cache-max-size", "64M", "--vfs-cache-max-age", "5m",
            "--vfs-read-ahead", "0", "--buffer-size", "0", "--dir-cache-time", "1s",
            "--vfs-read-chunk-size", "64k", "--vfs-read-chunk-size-limit", $"{chunkLimitKib}k",
            "--cache-dir", Path.Combine(directory, "cache"));
        if (caCertificate is not null)
        {
            start.ArgumentList.Add("--ca-cert");
            start.ArgumentList.Add(caCertificate);
        }
        if (bandwidthLimitKib > 0)
        {
            start.ArgumentList.Add("--bwlimit");
            start.ArgumentList.Add(bandwidthLimitKib.ToString(System.Globalization.CultureInfo.InvariantCulture) + "k");
        }
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool mounted = false;
        try
        {
            string file = Path.Combine(mountpoint, relativePath);
            using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(file))
            {
                if (process.HasExited)
                    throw new InvalidOperationException("rclone mount failed: " + await stderr);
                await Task.Delay(100, readyTimeout.Token);
            }

            mounted = true;
            object? emulator = null;
            object? emulatorServer = null;
            string? dolphin = Environment.GetEnvironmentVariable("ROMD_VFS_DOLPHIN");
            if (!string.IsNullOrEmpty(dolphin))
            {
                if (expected.Kind == "synthetic")
                    throw new InvalidOperationException("Dolphin requires ROMD_VFS_PROBE_FILE.");
                emulator = await VfsDolphinProbe.RunAsync(dolphin, file, Path.Combine(directory, "dolphin-user"));
                emulatorServer = metrics.Snapshot();
            }
            List<double> timings = await ReadMountedRandomAsync(file, expected);
            object firstPassServer = metrics.Snapshot();
            List<double> repeated = await ReadMountedRandomAsync(file, expected);
            object repeatedPassServer = metrics.Snapshot();

            long hashStarted = Stopwatch.GetTimestamp();
            await using (var stream = File.OpenRead(file))
                (await SHA256.HashDataAsync(stream)).ShouldBe(expected.Sha256);
            double fullHashSeconds = Stopwatch.GetElapsedTime(hashStarted).TotalSeconds;
            if (!string.IsNullOrEmpty(reportPath))
                report = JsonSerializer.Serialize(new
                {
                    recordedAt = DateTimeOffset.UtcNow,
                    workload = $"128 seeded random 4 KiB reads of {expected.Kind} content ({expected.Length} bytes), replayed once before whole-file hash",
                    transport = "rclone nfsmount over authenticated loopback WebDAV",
                    transportScheme = new Uri(url).Scheme,
                    requestDelayMilliseconds,
                    bandwidthLimitKib,
                    networkLimitation = "Optional fixed delay before each VFS request and rclone application-level bandwidth cap; no packet loss, TCP/TLS handshake delay or jitter is modeled. The direct HTTP probe has no client bandwidth cap.",
                    nfsReadAhead,
                    chunkLimitKib,
                    emulator,
                    emulatorServer,
                    emulatorWarmsClientBeforeRandomReads = emulator is not null,
                    cache = $"rclone byte cache {cacheMode}, initially empty, max-size target 64 MiB; server OS cache warm; client OS cache uncontrolled",
                    requests = timings.Count,
                    p50Milliseconds = timings[63],
                    p95Milliseconds = timings[121],
                    p99Milliseconds = timings[126],
                    fullHashMatched = true,
                    mountCleanedUp = true,
                    fullHashSeconds,
                    fullHashMiBPerSecond = expected.Length / 1048576d / fullHashSeconds,
                    fullHashLimitation = "Logical file bytes per elapsed second including previously cached bytes; not isolated wire throughput.",
                    replay = new { p50Milliseconds = repeated[63], p95Milliseconds = repeated[121], p99Milliseconds = repeated[126] },
                    firstPassServer,
                    repeatedPassServer,
                    directHttp = direct,
                    mountedServer = metrics.Snapshot(),
                    metricsScope = "Server snapshots are cumulative: setup and optional emulator run plus first pass, then both passes, then full hash. HTTP server duration includes authentication; VFS duration starts after middleware authorization."
                }, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            // Unmount only this probe's private read-only mount. Never delete through a live mount.
            var unmount = new ProcessStartInfo("/sbin/umount") { RedirectStandardError = true, UseShellExecute = false };
            unmount.ArgumentList.Add(mountpoint);
            using var cleanup = Process.Start(unmount)!;
            Task<string> cleanupError = cleanup.StandardError.ReadToEndAsync();
            await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            string error = await cleanupError;
            if (cleanup.ExitCode != 0 && (mounted || !process.HasExited))
                throw new InvalidOperationException("Probe mount cleanup failed; preserved " + mountpoint + ": " + error);
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr);
            if (cleanup.ExitCode == 0)
                Directory.Delete(directory, true);
        }
        if (report is not null)
            await File.WriteAllTextAsync(reportPath!, report);
    }

    private static async Task<List<double>> ReadMountedRandomAsync(string file, VfsProbeContent expected,
        CancellationToken ct = default)
    {
        var timings = new List<double>();
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        byte[] buffer = new byte[4096];
        var random = new Random(1729);
        for (int index = 0; index < 128; index++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = expected.NextOffset(random, buffer.Length);
            long start = Stopwatch.GetTimestamp();
            int read = 0;
            while (read < buffer.Length)
            {
                int count = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), offset + read, ct);
                if (count == 0)
                    throw new EndOfStreamException();
                read += count;
            }
            timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            buffer.ShouldBe(expected.ReadRange(offset, buffer.Length));
        }
        timings.Sort();
        return timings;
    }

    private sealed partial class ConsumerHostFixture
    {
        public async Task RemoveVfsLibraryAccessAsync(Guid userId)
        {
            await using var db = CreateSetupDbContext();
            await db.Users.Where(row => row.Id == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LibraryId, (int?)null));
        }

        public async Task<Func<Task>> ReplaceProbeContentAsync(int libraryId, VfsProbeContent source)
        {
            byte[] bytes = source.ReadRange(0, checked((int)source.Length));
            bytes[0] ^= 0xff;
            var cas = Factory.Services.GetRequiredService<IContentAddressableStore>();
            await using var input = new MemoryStream(bytes);
            var stored = await cas.StoreAsync(input);
            await using var db = CreateSetupDbContext();
            int releaseId = (await db.MaterializedLibraryReleases.SingleAsync(row => row.LibraryId == libraryId)).CatalogReleaseId!.Value;
            var catalog = await db.CatalogReleaseFiles.AsTracking().SingleAsync(row => row.CatalogReleaseId == releaseId);
            var originalHash = catalog.Sha1;
            var original = await db.RomFiles.SingleAsync(row => row.Sha1 == originalHash);
            var content = new FileEntityPersistence
            {
                Sha256 = Sha256.Parse(stored.Key.ToString()),
                Size = bytes.Length,
                SizeOnDisk = stored.StoredSize,
                IsCompressed = stored.IsCompressed,
                CreatedAt = original.CreatedAt
            };
            db.Files.Add(content);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            var replacementHash = Sha1.FromSpan(SHA1.HashData(bytes));
            db.StageOriginalRom(new OriginalRomFixture
            {
                OriginalFilename = original.OriginalFilename,
                FileId = content.Id,
                Sha1 = replacementHash,
                Md5 = Md5.FromSpan(MD5.HashData(bytes)),
                Crc32 = Crc32.FromUInt32(2),
                CreatedAt = original.CreatedAt
            });
            catalog.Sha1 = replacementHash;
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            // AuditInterceptor stamps inserts; force an identical stored timestamp to isolate hashes.
            await db.RomFiles.Where(row => row.Sha1 == replacementHash)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedAt, original.CreatedAt));
            return async () =>
            {
                await using var restore = CreateSetupDbContext();
                await restore.CatalogReleaseFiles.Where(row => row.Id == catalog.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Sha1, originalHash));
                await restore.RomFiles.Where(row => row.Sha1 == replacementHash).ExecuteDeleteAsync();
                await restore.Files.Where(row => row.Id == content.Id).ExecuteDeleteAsync();
            };
        }

        public async Task<string> SeedVfsProbeAsync(int libraryId, VfsProbeContent data,
            string? systemKey = null, CancellationToken ct = default)
        {
            var cas = Factory.Services.GetRequiredService<IContentAddressableStore>();
            data.Stream.Position = 0;
            var stored = await cas.StoreAsync(data.Stream, ct: ct);
            var sha1 = Sha1.FromSpan(data.Sha1);
            await using var db = CreateSetupDbContext();
            var now = DateTimeOffset.UtcNow;
            var platform = systemKey is null
                ? new PlatformEntity { Name = "Probe", ShortName = "probe", CreatedAt = now }
                : await db.Platforms.SingleOrDefaultAsync(row => row.CanonicalKey == systemKey, ct)
                    ?? new PlatformEntity
                    {
                        Name = systemKey == "gc" ? "GameCube" : "Wii",
                        ShortName = systemKey,
                        CanonicalKey = systemKey,
                        Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                        BuiltInVersion = 1,
                        BaseName = systemKey == "gc" ? "GameCube" : "Wii",
                        BaseCompactLabel = systemKey,
                        CreatedAt = now
                    };
            if (platform.Id == 0)
                db.Platforms.Add(platform);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync(ct);
            var title = new TitleEntity
            {
                PlatformId = platform.Id,
                Name = "Probe",
                NormalizedName = "probe",
                EnrichmentStatus = "Completed",
                CreatedAt = now
            };
            db.Titles.Add(title);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync(ct);
            var release = new CatalogReleaseEntity
            {
                CatalogTitleId = title.Id,
                PlatformId = platform.Id,
                Name = "Probe",
                Fingerprint = "vfs-probe",
                CreatedAt = now,
                UpdatedAt = now
            };
            var content = new FileEntityPersistence
            {
                Sha256 = Sha256.Parse(stored.Key.ToString()),
                Size = data.Length,
                SizeOnDisk = stored.StoredSize,
                IsCompressed = stored.IsCompressed,
                CreatedAt = now
            };
            db.CatalogReleases.Add(release);
            db.Files.Add(content);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync(ct);
            string name = "Disc/" + data.Name;
            db.StageOriginalRom(new OriginalRomFixture
            {
                OriginalFilename = name,
                FileId = content.Id,
                Sha1 = sha1,
                Md5 = Md5.FromSpan(data.Md5),
                Crc32 = Crc32.FromSpan(data.Crc32),
                CreatedAt = now
            });
            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
            {
                CatalogReleaseId = release.Id,
                FileFingerprint = sha1.ToString(),
                Name = name,
                Size = data.Length,
                Sha1 = sha1
            });
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = libraryId,
                TitleId = title.Id,
                PlatformId = platform.Id,
                CatalogReleaseId = release.Id,
                DatGameId = 1,
                DatFileId = 1,
                IsOwned = true,
                IsExposed = true,
                IsComplete = true,
                IsPlayable = true
            });
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync(ct);
            return IdCoder.Encode(platform.Id) + " " + Romd.Domain.Paths.PortablePathLabel.Create(platform.Name, "Platform") +
                "/" + IdCoder.Encode(release.Id) + " Probe/" + name;
        }
    }
}
