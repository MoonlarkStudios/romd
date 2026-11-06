using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Mount.Cli;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    private static async Task<object> ProbeRegisteredFskitAsync(string config, string caFile, string relativePath, VfsProbeContent expected, VfsConnectionFaultProbe? faults = null, string? freshPath = null, Func<Task>? replaceContent = null, TimeSpan? timeout = null, CancellationToken ct = default,
        VfsProbeContent? originalFormat = null, string? originalPath = null, Func<CancellationToken, Task>? changeFormat = null,
        Func<string, string>? redactDiagnostics = null)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("FSKit qualification requires macOS.");
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".romd", "qualification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string mountpoint = Path.Combine(directory, "mount");
        Directory.CreateDirectory(mountpoint);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        // Match the measured FSKit client profile: the largest range fits its 4 MiB buffer.
        foreach (string arg in new[] { "mount", "romd:", mountpoint, "--config", config, "--ca-cert", caFile,
            "--option", "backend=fskit", "--read-only", "--vfs-cache-mode", "full", "--cache-dir", Path.Combine(directory, "cache"),
            "--vfs-cache-max-size", originalFormat is not null ? "8G" : faults is null ? "256M" : "8M", "--vfs-cache-poll-interval", "1s", "--buffer-size", "4M", "--vfs-read-chunk-size", "1M",
            "--vfs-read-chunk-size-limit", "4M", "--dir-cache-time", "1s", "--timeout", "10s" })
            start.ArgumentList.Add(arg);
        if (originalFormat is not null)
        {
            start.ArgumentList.Add("--log-level");
            start.ArgumentList.Add("DEBUG");
        }
        Process? process = null;
        Task<VfsProbeDiagnosticCapture> stdout = Task.FromResult(new VfsProbeDiagnosticCapture("", 0));
        Task<VfsProbeDiagnosticCapture> stderr = Task.FromResult(new VfsProbeDiagnosticCapture("", 0));
        bool mountMayExist = false;
        long mountedDeadlineBegan = 0;
        async Task DrainAsync(string phase, CancellationToken token)
        {
            await Task.WhenAll(stdout, stderr);
            string? report = Environment.GetEnvironmentVariable("ROMD_VFS_CONNECTION_REPORT");
            if (redactDiagnostics is not null && !string.IsNullOrWhiteSpace(report))
            {
                var diagnostic = await stderr;
                await File.AppendAllTextAsync(report + ".mount-diagnostics.jsonl",
                    JsonSerializer.Serialize(new { phase, diagnostic = diagnostic.Text, diagnostic.DroppedLines }) + "\n", token);
            }
        }
        void CaptureDiagnostics(Process child)
        {
            Func<string, string> redact = redactDiagnostics ?? (static text => text);
            stdout = VfsProbeDiagnostics.DrainAsync(child.StandardOutput, redact, CancellationToken.None);
            stderr = VfsProbeDiagnostics.DrainAsync(child.StandardError, redact, CancellationToken.None);
        }
        async Task FinishProcessAsync(string phase, CancellationToken token)
        {
            Process owned = process!;
            bool reaped = false;
            await VfsProbeEvidence.RunAsync(async cleanupToken =>
            {
                await VfsProbeProcess.StopAsync(owned, cleanupToken);
                reaped = true;
                return true;
            }, cleanupToken => reaped ? DrainAsync(phase, cleanupToken) : Task.CompletedTask,
            _ =>
            {
                if (reaped)
                { owned.Dispose(); process = null; }
                return Task.CompletedTask;
            }, Console.Error.WriteLine, token);
        }
        async Task<byte[]> HashMountedAsync(Stream stream, string format, CancellationToken token)
        {
            long began = Stopwatch.GetTimestamp();
            double mountedElapsedAtStartSeconds = Stopwatch.GetElapsedTime(mountedDeadlineBegan).TotalSeconds;
            long bytes = 0;
            double lastProgressSeconds = 0;
            double maximumProgressIntervalSeconds = 0;
            bool completed = false;
            return await VfsProbeEvidence.RunAsync(async readToken =>
            {
                byte[] digest = await VfsProbeHash.ReadAsync(stream, count =>
                {
                    double elapsed = Stopwatch.GetElapsedTime(began).TotalSeconds;
                    maximumProgressIntervalSeconds = Math.Max(maximumProgressIntervalSeconds, elapsed - lastProgressSeconds);
                    lastProgressSeconds = elapsed;
                    bytes = count;
                }, readToken);
                completed = true;
                return digest;
            }, async evidenceToken =>
            {
                string? report = Environment.GetEnvironmentVariable("ROMD_VFS_CONNECTION_REPORT");
                if (!string.IsNullOrWhiteSpace(report))
                    await File.AppendAllTextAsync(report + ".hash-progress.jsonl",
                        JsonSerializer.Serialize(new
                        {
                            format,
                            bytes,
                            completed,
                            mountedElapsedAtStartSeconds,
                            mountedDeadlineSeconds = (timeout ?? TimeSpan.FromSeconds(90)).TotalSeconds,
                            lastProgressSeconds,
                            maximumProgressIntervalSeconds,
                            seconds = Stopwatch.GetElapsedTime(began).TotalSeconds
                        }) + "\n",
                        evidenceToken);
            }, _ => Task.CompletedTask, Console.Error.WriteLine, token);
        }
        async Task<VfsProbeProcessCompletion> UnmountAsync(CancellationToken token)
        {
            var unmount = new ProcessStartInfo("/sbin/umount")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            unmount.ArgumentList.Add(mountpoint);
            using var child = Process.Start(unmount)!;
            Task<string> output = child.StandardOutput.ReadToEndAsync();
            Task<string> error = child.StandardError.ReadToEndAsync();
            var result = await VfsProbeProcess.WaitAsync(child, TimeSpan.FromSeconds(15), token);
            await Task.WhenAll(output, error);
            return result;
        }
        return await VfsProbeEvidence.RunAsync<object>(async operationToken =>
        {
            process = Process.Start(start)!;
            mountMayExist = true;
            CaptureDiagnostics(process);
            string file = Path.Combine(mountpoint, relativePath);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
            mountedDeadlineBegan = Stopwatch.GetTimestamp();
            while (!File.Exists(file))
            {
                if (process.HasExited)
                    throw new InvalidOperationException("Registered FSKit mount did not start; check macFUSE readiness.");
                await Task.Delay(100, deadline.Token);
            }
            object? recovery = null;
            object? pressure = null;
            if (faults is not null)
            {
                expected.Length.ShouldBeGreaterThan(8L * 1024 * 1024, "Pressure probe requires ROMD_VFS_PROBE_MIB greater than 8.");
                faults.Interrupt();
                long interruptedAt = Stopwatch.GetTimestamp();
                bool originalReadCompleted = await VfsProbeRead.ObserveAsync(token => Task.Run(async () =>
                {
                    try
                    {
                        await using var stream = File.OpenRead(file);
                        byte[] bytes = new byte[4096];
                        await stream.ReadExactlyAsync(bytes, token);
                        bytes.ShouldBe(expected.ReadRange(0, bytes.Length));
                        return true;
                    }
                    catch (IOException) { return false; } // Transient read failure is allowed; corrupt bytes are not.
                }), async token =>
                {
                    await faults.Observed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }, faults.Restore, deadline.Token);
                await using (var stream = File.OpenRead(file))
                {
                    byte[] bytes = new byte[4096];
                    await stream.ReadExactlyAsync(bytes, deadline.Token);
                    bytes.ShouldBe(expected.ReadRange(0, bytes.Length));
                }
                process.HasExited.ShouldBeFalse();
                recovery = new
                {
                    faults.Aborts,
                    originalReadCompleted,
                    reopenedReadExact = true,
                    sameMountProcess = true,
                    seconds = Stopwatch.GetElapsedTime(interruptedAt).TotalSeconds,
                    injection = "Server aborts GET connections for at least two seconds; not WAN loss or process restart"
                };
            }
            // Read across EOF before hashing or random reads warm the tail of the cache.
            await using (var stream = File.OpenRead(file))
            {
                stream.Length.ShouldBe(expected.Length);
                stream.Position = expected.Length - 17;
                byte[] tail = new byte[4096];
                int count = await stream.ReadAsync(tail, deadline.Token);
                count.ShouldBe(17);
                tail.AsSpan(0, count).SequenceEqual(expected.ReadRange(expected.Length - 17, 17)).ShouldBeTrue();
                (await stream.ReadAsync(tail, deadline.Token)).ShouldBe(0);
            }
            var random = await ReadMountedRandomAsync(file, expected, deadline.Token);
            string? cachedFile = null;
            long allocatedWhileOpen = 0;
            // Hash in bounded large blocks and retain byte/time evidence on failure.
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                (await HashMountedAsync(stream, "stored", deadline.Token)).ShouldBe(expected.Sha256);
                if (faults is not null)
                {
                    cachedFile = Directory.EnumerateFiles(Path.Combine(directory, "cache"), "*", SearchOption.AllDirectories)
                        .Single(path => new FileInfo(path).Length == expected.Length);
                    allocatedWhileOpen = MacFileAllocation.Read(cachedFile).Allocated;
                    allocatedWhileOpen.ShouldBeGreaterThan(8L * 1024 * 1024);
                }
            }
            bool? sameSizeReplacementExact = null;
            bool? sameMountReplacementExact = null;
            object? formatChange = null;
            if (originalFormat is not null && originalPath is not null && changeFormat is not null)
            {
                // Qualify a settled cache; rapid replacement during rclone's close grace
                // period remains the separate documented client limitation.
                await Task.Delay(TimeSpan.FromSeconds(6), deadline.Token);
                string retainedCacheFile = Directory.EnumerateFiles(Path.Combine(directory, "cache"), "*", SearchOption.AllDirectories)
                    .Single(path => new FileInfo(path).Length == expected.Length);
                byte[] retainedCacheHash;
                await using (var cached = File.OpenRead(retainedCacheFile))
                    retainedCacheHash = await SHA256.HashDataAsync(cached, deadline.Token);
                retainedCacheHash.ShouldBe(expected.Sha256);
                long retainedCacheAllocatedBytes = MacFileAllocation.Read(retainedCacheFile).Allocated;
                await changeFormat(deadline.Token);
                await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token);
                string originalFile = Path.Combine(mountpoint, originalPath);
                string[] refreshed = Directory.GetFiles(Path.GetDirectoryName(file)!);
                bool sameMountRenameVisible = refreshed.Contains(originalFile) && !refreshed.Contains(file);
                var unmounted = await UnmountAsync(deadline.Token);
                unmounted.TimedOut.ShouldBeFalse();
                unmounted.Canceled.ShouldBeFalse();
                unmounted.ExitCode.ShouldBe(0);
                mountMayExist = false;
                await FinishProcessAsync("stored-before-format-remount", CancellationToken.None);
                File.Exists(retainedCacheFile).ShouldBeTrue("Keep the same stored-format disk cache entry across remount.");
                new FileInfo(retainedCacheFile).Length.ShouldBe(expected.Length);
                await using (var cached = File.OpenRead(retainedCacheFile))
                    (await SHA256.HashDataAsync(cached, deadline.Token)).ShouldBe(retainedCacheHash);
                MacFileAllocation.Read(retainedCacheFile).Allocated.ShouldBe(retainedCacheAllocatedBytes);
                process = Process.Start(start)!;
                mountMayExist = true;
                CaptureDiagnostics(process);
                while (!File.Exists(originalFile))
                {
                    process.HasExited.ShouldBeFalse();
                    await Task.Delay(100, deadline.Token);
                }
                File.Exists(file).ShouldBeFalse("The original setting must not retain the stored path as an alias.");
                await using (var stream = File.OpenRead(originalFile))
                {
                    stream.Length.ShouldBe(originalFormat.Length);
                    stream.Position = originalFormat.Length - 17;
                    byte[] tail = new byte[4096];
                    int count = await stream.ReadAsync(tail, deadline.Token);
                    count.ShouldBe(17);
                    tail.AsSpan(0, count).SequenceEqual(originalFormat.ReadRange(originalFormat.Length - 17, 17)).ShouldBeTrue();
                    (await stream.ReadAsync(tail, deadline.Token)).ShouldBe(0);
                }
                var originalRandom = await ReadMountedRandomAsync(originalFile, originalFormat, deadline.Token);
                await using (var stream = new FileStream(originalFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    (await HashMountedAsync(stream, "original", deadline.Token)).ShouldBe(originalFormat.Sha256);
                formatChange = new
                {
                    sameMountRenameVisible,
                    remountedRenameExact = true,
                    retainedDiskCache = true,
                    retainedCacheHash = Convert.ToHexString(retainedCacheHash).ToLowerInvariant(),
                    retainedCacheAllocatedBytes,
                    retainedCacheSamePath = true,
                    retainedCacheSameBytes = true,
                    originalFullHashExact = true,
                    originalEofExact = true,
                    originalRandomReadCount = originalRandom.Count,
                    originalRandomReadP50Milliseconds = originalRandom[63],
                    originalRandomReadP95Milliseconds = originalRandom[121],
                    storedSha256 = Convert.ToHexString(expected.Sha256).ToLowerInvariant(),
                    originalSha256 = Convert.ToHexString(originalFormat.Sha256).ToLowerInvariant(),
                    bufferBytes = 4L << 20,
                    maximumReadChunkBytes = 4L << 20,
                    http2Enabled = true,
                    cacheTargetBytes = 8L << 30,
                    settlingSeconds = 6,
                    mountedDeadlineSeconds = (timeout ?? TimeSpan.FromSeconds(90)).TotalSeconds
                };
            }
            if (replaceContent is not null)
            {
                await replaceContent();
                // Let directory/attribute caches expire, then refresh metadata before reopening.
                await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token);
                _ = Directory.GetFiles(Path.GetDirectoryName(file)!);
                byte[] replacement = expected.ReadRange(0, checked((int)expected.Length));
                replacement[0] ^= 0xff;
                await using (var current = File.OpenRead(file))
                    sameMountReplacementExact = (await SHA256.HashDataAsync(current, deadline.Token))
                        .AsSpan().SequenceEqual(SHA256.HashData(replacement));
                // Preserve rclone's disk cache across remount. This separates cache-validator
                // correctness from FSKit/kernel page caching of same-size, same-mtime files.
                var unmounted = await UnmountAsync(deadline.Token);
                unmounted.TimedOut.ShouldBeFalse();
                unmounted.Canceled.ShouldBeFalse();
                unmounted.ExitCode.ShouldBe(0);
                mountMayExist = false;
                await FinishProcessAsync("before-content-remount", CancellationToken.None);
                Directory.EnumerateFiles(Path.Combine(directory, "cache"), "*", SearchOption.AllDirectories)
                    .Any(path => new FileInfo(path).Length == expected.Length).ShouldBeTrue("The disk cache must survive remount.");
                process = Process.Start(start)!;
                mountMayExist = true;
                CaptureDiagnostics(process);
                while (!File.Exists(file))
                {
                    process.HasExited.ShouldBeFalse();
                    await Task.Delay(100, deadline.Token);
                }
                await using var stream = File.OpenRead(file);
                (await SHA256.HashDataAsync(stream, deadline.Token)).ShouldBe(SHA256.HashData(replacement),
                    "A same-size catalog replacement must invalidate the warm VFS cache without deleting it.");
                sameSizeReplacementExact = true;
            }
            if (faults is not null)
            {
                long closedAt = Stopwatch.GetTimestamp();
                using var evictionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (File.Exists(cachedFile))
                    await Task.Delay(200, evictionDeadline.Token);
                double evictionSeconds = Stopwatch.GetElapsedTime(closedAt).TotalSeconds;
                long getsBefore = faults.Gets;
                await using (var stream = File.OpenRead(Path.Combine(mountpoint, freshPath!)))
                    (await SHA256.HashDataAsync(stream, deadline.Token)).ShouldBe(expected.Sha256);
                long freshGets = faults.Gets - getsBefore;
                freshGets.ShouldBeGreaterThan(0);
                process.HasExited.ShouldBeFalse();
                pressure = new
                {
                    targetBytes = 8L * 1024 * 1024,
                    allocatedWhileOpen,
                    evictionSeconds,
                    closedFileEvicted = true,
                    freshPathHashExact = true,
                    freshGets,
                    scope = "Soft cache-size target; distinct client path with same CAS bytes; no free-disk exhaustion"
                };
            }
            Exception? writeError = null;
            try
            { await File.WriteAllTextAsync(Path.Combine(mountpoint, "write-must-fail.txt"), "probe", deadline.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeError = error; }
            writeError.ShouldNotBeNull();
            File.Exists(Path.Combine(mountpoint, "write-must-fail.txt")).ShouldBeFalse();
            return new
            {
                recovery,
                pressure,
                formatChange,
                sameSizeReplacementExact,
                sameMountReplacementExact,
                backend = "macfuse-fskit",
                privateParent = true,
                readOnly = true,
                eofExact = true,
                eofBeforeOtherReads = faults is null,
                fullHashExact = true,
                writeRejected = true,
                randomReadCount = random.Count,
                randomReadP50Milliseconds = random[63],
                randomReadP95Milliseconds = random[121],
                scope = "Owner-only functional reads and write rejection; no second-user isolation or restart qualification"
            };
        }, _ => Task.CompletedTask, async cleanupToken =>
        {
            await VfsProbeEvidence.RunAsync(async token =>
            {
                // Never recurse into a live or unproven filesystem. Cleanup has its own
                // deadline; cancellation cannot abandon an owned umount child.
                if (mountMayExist)
                {
                    var cleanup = await UnmountAsync(token);
                    if (cleanup.TimedOut || cleanup.Canceled || cleanup.ExitCode != 0)
                    {
                        Console.Error.WriteLine($"FSKit cleanup unresolved; preserved mount/cache at {mountpoint}.");
                        throw new InvalidOperationException($"FSKit cleanup unresolved; preserved mount/cache at {mountpoint}; rclone PID {process?.Id}; umount PID {cleanup.ProcessId}, exit {cleanup.ExitCode}, timeout {cleanup.TimedOut}.");
                    }
                    mountMayExist = false;
                }
                if (process is not null)
                    await FinishProcessAsync("final-cleanup", token);
                return true;
            }, _ => Task.CompletedTask, _ =>
            {
                if (!mountMayExist && process is null)
                    Directory.Delete(directory, recursive: true);
                return Task.CompletedTask;
            }, Console.Error.WriteLine, cleanupToken);
        }, Console.Error.WriteLine, ct);
    }
}
