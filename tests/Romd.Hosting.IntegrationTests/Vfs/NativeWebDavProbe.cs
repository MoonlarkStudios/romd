using System.Diagnostics;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Hosting;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class NativeWebDavFactAttribute : FactAttribute
{
    public NativeWebDavFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_NATIVE_WEBDAV_TEST") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")) ||
            !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_PYTHON")))
            Skip = "Native WebDAV requires macOS, ROMD_NATIVE_WEBDAV_TEST=1, ROMD_VFS_RCLONE and ROMD_VFS_PYTHON; it mounts a disposable loopback fixture.";
    }
}

internal static class NativeWebDavProbe
{
    public static async Task RunAsync(string url, string relativePath, VfsProbeContent source,
        NativeWebDavAuthenticationProbe authentication)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ROMD_VFS_PYTHON")!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(HangfirePostgreSqlTestSupport.RepositoryRoot,
            "scripts", "mount", "native_webdav_probe.py"));
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        // Credentials travel through stdin, never arguments, logs, or reports.
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
        {
            url,
            relativePath,
            password = authentication.Password,
            length = source.Length,
            sha256 = Convert.ToHexString(source.Sha256).ToLowerInvariant(),
            report = Environment.GetEnvironmentVariable("ROMD_NATIVE_WEBDAV_REPORT")
        }));
        process.StandardInput.Close();
        // The probe owns its bounded operation deadline and mount cleanup.
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, await output + await error);
    }
}
