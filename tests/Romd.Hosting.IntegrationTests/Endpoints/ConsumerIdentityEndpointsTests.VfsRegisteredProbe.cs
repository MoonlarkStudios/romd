using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Consumer.Account;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    private static async Task ProbeRegisteredConnectionsAsync(HttpClient owner, ConsumerHostFixture fixture,
        VfsProbeContent source, string relativePath, string caFile, bool mount, VfsConnectionFaultProbe? faults = null, string? freshPath = null, Func<Task<Func<Task>>>? replaceContent = null,
        VfsProbeContent? originalFormat = null, string? originalPath = null, object? mediaTransition = null, CancellationToken ct = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-registered-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var secrets = new List<string>();
        var timings = new List<double>();
        byte[] expectedRange = source.ReadRange(65530, 4096);
        using var metrics = new VfsProbeMetrics();
        long rejections = 0;
        using var listener = new MeterListener();
        bool completed = false;
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "romd.vfs.admission_rejected")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref rejections, value));
        listener.Start();
        await VfsProbeEvidence.RunAsync(async _ =>
        {
            async Task<VfsConnectionCredentialDto> Issue(string path, object? body)
            {
                using var response = body is null ? await owner.PostAsync(path, null) : await owner.PostAsJsonAsync(path, body);
                response.StatusCode.ShouldBe(path == "/api/me/connections" ? HttpStatusCode.Created : HttpStatusCode.OK);
                var issued = (await response.Content.ReadFromJsonAsync<VfsConnectionCredentialDto>())!;
                secrets.Add(issued.Password);
                secrets.Add(issued.RcloneConfig.Split("pass = ")[1].Trim());
                secrets.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(issued.Connection.Username + ":" + issued.Password)));
                return issued;
            }
            async Task<string> Save(string name, VfsConnectionCredentialDto credential)
            {
                string path = Path.Combine(directory, name + ".conf");
                // Test the exported document verbatim; private parent protects the creation window.
                await File.WriteAllTextAsync(path, credential.RcloneConfig);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return path;
            }
            async Task<(int ExitCode, byte[] Bytes)> Run(string config, string[] arguments, bool hashOutput = false, int lowLevelRetries = 1)
            {
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.Ordinal)).ToArray())
                    start.Environment.Remove(key);
                foreach (string arg in arguments)
                    start.ArgumentList.Add(arg);
                foreach (string arg in new[] { "--config", config, "--ca-cert", caFile, "--retries", "1", "--low-level-retries", lowLevelRetries.ToString(System.Globalization.CultureInfo.InvariantCulture), "--timeout", "10s" })
                    start.ArgumentList.Add(arg);
                long began = Stopwatch.GetTimestamp();
                using var process = Process.Start(start)!;
                using var bytes = new MemoryStream();
                Task<byte[]>? hash = hashOutput ? SHA256.HashDataAsync(process.StandardOutput.BaseStream).AsTask() : null;
                Task output = hash ?? process.StandardOutput.BaseStream.CopyToAsync(bytes);
                Task<string> errors = process.StandardError.ReadToEndAsync();
                var completion = await VfsProbeProcess.WaitAsync(process,
                    TimeSpan.FromSeconds(originalFormat is null ? 30 : 180), ct);
                await output;
                string error = await errors;
                ct.ThrowIfCancellationRequested();
                completion.TimedOut.ShouldBeFalse($"Rclone client PID {process.Id} exceeded the probe deadline and was killed/reaped.");
                // Do not place raw stderr in assertions: a failing client could print credentials.
                foreach (string secret in secrets)
                    error.Contains(secret, StringComparison.Ordinal).ShouldBeFalse("Client diagnostics must not expose credentials.");
                lock (timings)
                    timings.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                return (process.ExitCode, hash is null ? bytes.ToArray() : await hash);
            }
            async Task Read(string config)
            {
                var result = await Run(config, ["cat", "romd:" + relativePath, "--offset", "65530", "--count", "4096"]);
                result.ExitCode.ShouldBe(0);
                result.Bytes.ShouldBe(expectedRange);
            }
            async Task Denied(string config)
            {
                var result = await Run(config, ["cat", "romd:" + relativePath, "--count", "4096"]);
                result.ExitCode.ShouldNotBe(0);
                result.Bytes.ShouldBeEmpty();
            }

            var deck = await Issue("/api/me/connections", new SaveVfsConnectionRequest("Probe Deck"));
            var laptop = await Issue("/api/me/connections", new SaveVfsConnectionRequest("Probe Laptop"));
            string deckConfig = await Save("deck", deck);
            string laptopConfig = await Save("laptop", laptop);
            string deckPath = $"/api/me/connections/{deck.Connection.Id}";
            async Task SetFormat(string format, CancellationToken token)
            {
                using var response = await owner.PatchAsJsonAsync(deckPath, new { name = deck.Connection.Name, format }, token);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                document.RootElement.GetProperty("format").GetString().ShouldBe(format);
            }
            if (replaceContent is not null)
                await ProbeRcloneCacheFreshnessAsync(deckConfig, caFile, relativePath, source, replaceContent);
            if (originalFormat is not null)
            {
                await SetFormat("original", ct);
                var directOriginal = await Run(deckConfig, ["cat", "romd:" + originalPath], hashOutput: true);
                directOriginal.ExitCode.ShouldBe(0);
                directOriginal.Bytes.ShouldBe(originalFormat.Sha256);
                await SetFormat("stored", ct);
            }
            string Redact(string text)
            {
                foreach (string secret in secrets)
                    text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
                return text;
            }
            Func<Task>? restoreContent = null;
            object? mounted;
            try
            {
                mounted = mount ? await ProbeRegisteredFskitAsync(deckConfig, caFile, relativePath, source, faults, freshPath,
                    replaceContent is null ? null : async () => { restoreContent = await replaceContent(); },
                    timeout: originalFormat is null ? null : TimeSpan.FromMinutes(3), ct: ct,
                    originalFormat: originalFormat, originalPath: originalPath,
                    changeFormat: originalFormat is null ? null : token => SetFormat("original", token),
                    redactDiagnostics: Redact) : null;
                if (originalFormat is not null)
                {
                    var originalListing = await Run(deckConfig, ["lsjson", "romd:", "--recursive", "--files-only"]);
                    originalListing.ExitCode.ShouldBe(0);
                    using var entries = JsonDocument.Parse(originalListing.Bytes);
                    var paths = entries.RootElement.EnumerateArray().Select(entry => entry.GetProperty("Path").GetString()).ToArray();
                    paths.ShouldContain(originalPath);
                    paths.ShouldNotContain(relativePath);
                    var decoded = await Run(deckConfig, ["cat", "romd:" + originalPath], hashOutput: true);
                    decoded.ExitCode.ShouldBe(0);
                    decoded.Bytes.ShouldBe(originalFormat.Sha256);
                    await SetFormat("stored", ct);
                }
            }
            finally { if (restoreContent is not null) await restoreContent(); }
            var listing = await Run(deckConfig, ["lsjson", "romd:", "--recursive", "--files-only"]);
            listing.ExitCode.ShouldBe(0);
            using (var entries = JsonDocument.Parse(listing.Bytes))
                entries.RootElement.EnumerateArray().Select(entry => entry.GetProperty("Path").GetString()).ShouldContain(relativePath);
            // rclone 1.75.1 treats MKCOL 405 as "already exists" and can report success.
            // Verify absence independently; actual uploads must fail as well.
            var mkdir = await Run(deckConfig, ["mkdir", "romd:write-must-fail"]);
            var absent = await Run(deckConfig, ["lsjson", "romd:write-must-fail"]);
            absent.ExitCode.ShouldNotBe(0);
            string writeSource = Path.Combine(directory, "write.bin");
            await File.WriteAllBytesAsync(writeSource, [1, 2, 3]);
            var write = await Run(deckConfig, ["copyto", writeSource, "romd:write-must-fail.bin"]);
            write.ExitCode.ShouldNotBe(0);
            var full = await Run(deckConfig, ["cat", "romd:" + relativePath], hashOutput: true);
            full.ExitCode.ShouldBe(0);
            full.Bytes.ShouldBe(source.Sha256);
            await Read(deckConfig);
            var eof = await Run(deckConfig, ["cat", "romd:" + relativePath, "--offset",
                (source.Length - 17).ToString(System.Globalization.CultureInfo.InvariantCulture), "--count", "4096"]);
            eof.ExitCode.ShouldBe(0);
            eof.Bytes.ShouldBe(source.ReadRange(source.Length - 17, 17));

            // Hold all slots until both real clients are queued; this makes contention observable.
            var admission = fixture.Factory.Services.GetRequiredService<ConsumerVfsAdmission>();
            var blockers = new List<IDisposable>();
            try
            {
                for (int i = 0; i < 8; i++)
                    blockers.Add((await admission.EnterAsync(Guid.NewGuid(), Guid.NewGuid(), default))!);
                var concurrent = new[] { Read(deckConfig), Read(laptopConfig) };
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (admission.QueuedRequestCount < 2)
                    await Task.Delay(10, deadline.Token);
                foreach (var blocker in blockers)
                    blocker.Dispose();
                blockers.Clear();
                await Task.WhenAll(concurrent);
            }
            finally { foreach (var blocker in blockers) blocker.Dispose(); }

            var options = fixture.Factory.Services.GetRequiredService<ConsumerVfsOptions>();
            int originalWait = options.MaximumQueueWaitMilliseconds;
            long beforeOverload = Interlocked.Read(ref rejections);
            try
            {
                options.MaximumQueueWaitMilliseconds = 200;
                for (int i = 0; i < options.MaximumConcurrentRequests; i++)
                    blockers.Add((await admission.EnterAsync(Guid.NewGuid(), Guid.NewGuid(), default))!);
                var overloaded = await Run(deckConfig, ["cat", "romd:" + relativePath, "--count", "4096"], lowLevelRetries: 2);
                overloaded.ExitCode.ShouldNotBe(0);
                overloaded.Bytes.ShouldBeEmpty();
                (Interlocked.Read(ref rejections) - beforeOverload).ShouldBeGreaterThanOrEqualTo(2);
            }
            finally
            {
                options.MaximumQueueWaitMilliseconds = originalWait;
                foreach (var blocker in blockers)
                    blocker.Dispose();
                blockers.Clear();
            }
            await Read(deckConfig);

            var replacement = await Issue(deckPath + "/rotate", null);
            string replacementConfig = await Save("replacement", replacement);
            await Denied(deckConfig);
            await Read(replacementConfig);
            await Read(laptopConfig);
            using (var revoke = await owner.DeleteAsync(deckPath))
                revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            await Denied(replacementConfig);
            await Read(laptopConfig);
            using (var password = await owner.PutAsJsonAsync("/api/me/password", new ChangePasswordRequest("Password123", "Password456")))
                password.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            await Denied(laptopConfig);

            string? reportPath = Environment.GetEnvironmentVariable("ROMD_VFS_CONNECTION_REPORT");
            if (!string.IsNullOrEmpty(reportPath))
            {
                using var executable = File.OpenRead(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")!);
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                {
                    recordedAt = DateTimeOffset.UtcNow,
                    runtime = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    rcloneSha256 = Convert.ToHexString(await SHA256.HashDataAsync(executable)),
                    source.Kind,
                    contentBytes = source.Length,
                    workload = "Loopback HTTPS; verbatim exports; listing, unchanged namespace after mkdir, rejected upload, full hash, range, EOF, two deliberately queued clients, timed overload with retries and recovery, rotation, revocation, password invalidation",
                    qualification = "Loopback protocol and credential flow; mounted evidence only when mounted is non-null; no WAN, reboot, gameplay, or throughput tuning",
                    mounted,
                    mediaTransition,
                    originalFormatFullHashExact = originalFormat is not null ? true : (bool?)null,
                    mediaProcessDeadlineSeconds = originalFormat is null ? (int?)null : 180,
                    rcloneVfsSameSizeSameTimeRefresh = replaceContent is not null ? true : (bool?)null,
                    passed = true,
                    mkdirExitCode = mkdir.ExitCode,
                    mkdirCreated = false,
                    uploadRejected = true,
                    overloadRejections = Interlocked.Read(ref rejections) - beforeOverload,
                    processMilliseconds = timings.ToArray(),
                    responseBodyBytes = metrics.ResponseBytes,
                    server = metrics.Snapshot()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            completed = true;
            return true;
        }, async evidenceToken =>
        {
            string? reportPath = Environment.GetEnvironmentVariable("ROMD_VFS_CONNECTION_REPORT");
            if (!string.IsNullOrEmpty(reportPath))
                await File.WriteAllTextAsync(reportPath + ".server-metrics.json", JsonSerializer.Serialize(new
                {
                    completed,
                    responseBodyBytes = metrics.ResponseBytes,
                    server = metrics.Snapshot(),
                    processMilliseconds = timings.ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }), evidenceToken);
        }, _ =>
        {
            Directory.Delete(directory, recursive: true);
            return Task.CompletedTask;
        }, Console.Error.WriteLine, ct);
    }
}
