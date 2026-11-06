using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsProbeProcessTests
{
    [Fact]
    public async Task WaitAsync_ExitedChild_ReturnsExitWithoutTimeout()
    {
        using var child = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        var result = await VfsProbeProcess.WaitAsync(child, TimeSpan.FromSeconds(15), CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        result.ShouldBe(new VfsProbeProcessCompletion(child.Id, 0, false, false));
        child.HasExited.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitAsync_DeadlineOrCancellation_KillsAndReapsBeforeReturning(bool cancel)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping.exe" : "/bin/sleep")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in OperatingSystem.IsWindows() ? new[] { "-n", "60", "127.0.0.1" } : new[] { "30" })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var result = await VfsProbeProcess.WaitAsync(child,
            cancel ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(100), cancellation.Token);
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        result.Canceled.ShouldBe(cancel);
        result.TimedOut.ShouldBe(!cancel);
        child.HasExited.ShouldBeTrue();
        stdout.IsCompletedSuccessfully.ShouldBeTrue();
        stderr.IsCompletedSuccessfully.ShouldBeTrue();
    }
}
