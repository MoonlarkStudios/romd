using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Mount.Credentials;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    private static async Task ProbePackagedLifecycleAsync(
        Func<string, string?, Process> start,
        Func<string, string?, Task<(int Code, string Output)>> command,
        VfsProbeContent source, string relativePath, string directory, string key,
        RevocationFailureFilter revocationFailure)
    {
        // Credentials stay inside the packaged app's Keychain identity. Unlike
        // SDK-hosted tests, this probe must not read them from the test process.
        var status = await command("status", null);
        status.Code.ShouldBe(0, status.Output);
        string mountpoint = Path.Combine(directory, "mounted");
        string reservation = Path.Combine(MountProfileLease.CredentialLockDirectory, "mounts", key + ".json");
        foreach (bool crash in new[] { false, true })
        {
            using var mounted = start("mount", null);
            Task<string> error = mounted.StandardError.ReadToEndAsync();
            bool recovered = false;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                bool ready = false;
                bool cacheSettings = false;
                while (await mounted.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    if (line.StartsWith("Cache target: 8 MiB; minimum free space target: 1 MiB", StringComparison.Ordinal))
                        cacheSettings = true;
                    if (line.StartsWith("Mounted read-only at ", StringComparison.Ordinal))
                    { ready = true; break; }
                }
                if (!ready)
                    throw new InvalidOperationException("Packaged mount did not become ready: " + await error.WaitAsync(TimeSpan.FromSeconds(5)));
                cacheSettings.ShouldBeTrue();
                await using (var file = File.OpenRead(Path.Combine(mountpoint, relativePath)))
                    (await SHA256.HashDataAsync(file, deadline.Token)).ShouldBe(source.Sha256);
                if (crash)
                {
                    using var record = JsonDocument.Parse(await File.ReadAllTextAsync(reservation));
                    int childPid = record.RootElement.GetProperty("ChildPid").GetInt32();
                    string brokerDirectory = record.RootElement.GetProperty("BrokerDirectory").GetProperty("Directory").GetString()!;
                    using var orphan = Process.GetProcessById(childPid);
                    mounted.Kill();
                    await mounted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    orphan.HasExited.ShouldBeFalse();
                    var recovery = await command("recover", null);
                    recovery.Code.ShouldBe(0, recovery.Output);
                    recovered = true;
                    await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Directory.Exists(brokerDirectory).ShouldBeFalse();
                }
            }
            finally
            {
                if (!mounted.HasExited)
                {
                    var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                    signal.ArgumentList.Add("-TERM");
                    signal.ArgumentList.Add(mounted.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    using var stopping = Process.Start(signal)!;
                    await stopping.WaitForExitAsync();
                    await mounted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                if (File.Exists(reservation))
                {
                    var recovery = await command("recover", null);
                    recovery.Code.ShouldBe(0, recovery.Output);
                    recovered = true;
                }
                if (recovered)
                    Directory.Delete(mountpoint); // Successful recovery leaves only the empty mountpoint.
            }
            if (!crash)
                mounted.ExitCode.ShouldBe(0, await error);
            Directory.Exists(mountpoint).ShouldBeFalse();
            status = await command("status", null);
            status.Code.ShouldBe(0, status.Output); // A fresh packaged process loads and refreshes saved credentials.
        }
        revocationFailure.Enabled = true;
        try
        { (await command("logout", null)).Code.ShouldBe(3); }
        finally { revocationFailure.Enabled = false; }
        (await command("status", null)).Code.ShouldBe(0);
        (await command("logout", null)).Code.ShouldBe(0);
        (await command("status", null)).Code.ShouldBe(3);
        (await command("logout", null)).Code.ShouldBe(0);
    }

    public sealed class PackagedMountFactAttribute : FactAttribute
    {
        public PackagedMountFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1" ||
                Environment.GetEnvironmentVariable("ROMD_VFS_MOUNT") != "1" || !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_MOUNT_CLI")))
                Skip = "Requires macOS, ROMD_MOUNT_KEYCHAIN_TEST=1, ROMD_VFS_MOUNT=1 and a self-contained ROMD_VFS_MOUNT_CLI bundle launcher.";
        }
    }
}
