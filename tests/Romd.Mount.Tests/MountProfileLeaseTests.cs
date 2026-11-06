using System.Diagnostics;
using Romd.Mount.Credentials;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountProfileLeaseTests
{
    [Fact]
    public async Task LiveOwner_ExcludesAnotherProcess_AndCrashReleasesOwnership()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-mount-lease-" + Guid.NewGuid().ToString("N"));
        string key = new('a', 64);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(TestHost.Program).Assembly.Location);
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(key);
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("READY");
            Should.Throw<IOException>(() => MountProfileLease.Acquire(directory, key));
            using (MountProfileLease.Acquire(directory, new string('b', 64)))
            { }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using (MountProfileLease.Acquire(directory, key))
            { }
            File.Exists(Path.Combine(directory, key + ".lock")).ShouldBeTrue();
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await errors;
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("")]
    [InlineData("ABCDEF")]
    public void InvalidKey_RejectsBeforeCreatingDirectory(string key)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Should.Throw<ArgumentException>(() => MountProfileLease.Acquire(directory, key));
        Directory.Exists(directory).ShouldBeFalse();
    }
}
