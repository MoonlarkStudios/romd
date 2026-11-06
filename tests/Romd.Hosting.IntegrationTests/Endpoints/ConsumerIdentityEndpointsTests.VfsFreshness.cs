using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Romd.Hosting.IntegrationTests.Vfs;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    // Exercise rclone's VFS disk cache directly, independently of an OS page cache.
    private static async Task ProbeRcloneCacheFreshnessAsync(string config, string caFile, string relativePath,
        VfsProbeContent source, Func<Task<Func<Task>>> replace)
    {
        string cache = Path.Combine(Path.GetTempPath(), "romd-cache-freshness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        foreach (string arg in new[] { "serve", "http", "romd:", "--addr", $"127.0.0.1:{port}",
            "--config", config, "--ca-cert", caFile, "--read-only", "--vfs-cache-mode", "full",
            "--cache-dir", cache, "--dir-cache-time", "1s", "--timeout", "10s" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Func<Task>? restore = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            while (true)
            {
                process.HasExited.ShouldBeFalse("rclone's isolated VFS HTTP probe must start.");
                try
                { using var response = await client.GetAsync("/", deadline.Token); if (response.IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
            string path = "/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
            async Task<byte[]> Hash()
            {
                await using var stream = await client.GetStreamAsync(path, deadline.Token);
                return await SHA256.HashDataAsync(stream, deadline.Token);
            }
            (await Hash()).ShouldBe(source.Sha256);
            Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)
                .Any(file => new FileInfo(file).Length == source.Length).ShouldBeTrue();
            // Allow rclone to close its cached descriptor after the download grace period.
            await Task.Delay(TimeSpan.FromSeconds(6), deadline.Token);
            restore = await replace();
            await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token);
            using var refresh = await client.GetAsync(path[..(path.LastIndexOf('/') + 1)], deadline.Token);
            refresh.EnsureSuccessStatusCode();
            byte[] replacement = source.ReadRange(0, checked((int)source.Length));
            replacement[0] ^= 0xff;
            (await Hash()).ShouldBe(SHA256.HashData(replacement), "The same running rclone VFS must invalidate same-size, same-time disk cache entries.");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (restore is not null)
                await restore();
            Directory.Delete(cache, recursive: true);
        }
    }
}
