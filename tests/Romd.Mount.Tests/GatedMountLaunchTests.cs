using System.Diagnostics;
using Romd.Mount.Mounting;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class GatedMountLaunchTests
{
    [TokenCommandTests.UnixFact]
    public async Task Gate_EofWithoutRelease_DoesNotExecuteTarget()
    {
        var start = Start("/usr/bin/printf", "TARGET_RAN");
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        process.ExitCode.ShouldBe(125);
        (await output).ShouldBeEmpty();
    }

    [TokenCommandTests.UnixFact]
    public async Task Gate_Release_PreservesLiteralArguments()
    {
        const string literal = "spaces ' \" $(echo must-not-run) ; `echo nope`";
        using var process = Process.Start(Start("/usr/bin/printf", "%s", literal))!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        await GatedMountLaunch.ReleaseAsync(process, default);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        process.ExitCode.ShouldBe(0);
        (await output).ShouldBe(literal);
    }

    [MacStateAclTests.MacAclFact]
    public async Task Gate_BeforeExec_RecoveryRequiresExplicitLauncherEvidence()
    {
        using var process = Process.Start(Start("/bin/sleep", "120"))!;
        try
        {
            DateTime started = process.StartTime.ToUniversalTime();
            var host = new MacMountRecoveryHost();
            await Should.ThrowAsync<IOException>(() => host.StopChildAsync(process.Id, started, "/bin/sleep", default));
            process.HasExited.ShouldBeFalse();
            await host.StopChildAsync(process.Id, started, "/bin/sleep", default, allowLauncher: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    [MacStateAclTests.MacAclFact]
    public async Task Gate_Exec_PreservesRecordedProcessIdentity()
    {
        using var process = Process.Start(Start("/bin/sleep", "120"))!;
        try
        {
            int pid = process.Id;
            DateTime started = process.StartTime.ToUniversalTime();
            await GatedMountLaunch.ReleaseAsync(process, default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                using var observed = Process.GetProcessById(pid);
                if (observed.MainModule?.FileName == "/bin/sleep")
                {
                    observed.StartTime.ToUniversalTime().ShouldBe(started);
                    break;
                }
                await Task.Delay(10, deadline.Token);
            }
            await new MacMountRecoveryHost().StopChildAsync(pid, started, "/bin/sleep", default);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    private static ProcessStartInfo Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        GatedMountLaunch.Configure(start);
        return start;
    }
}
