using System.Diagnostics;
using Romd.Mount.Credentials;
using Romd.Mount.Mounting;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountReservationTests
{
    [MacStateAclTests.MacAclFact]
    public async Task GatedOwnerCrashBeforeChildRecord_ChildExitsAndReservationCanRecover()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-gated-recovery-" + Guid.NewGuid().ToString("N"));
        string key = new('b', 64);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { typeof(TestHost.Program).Assembly.Location, directory, key, "reserve-gated" })
            start.ArgumentList.Add(arg);
        using var owner = Process.Start(start)!;
        Task<string> errors = owner.StandardError.ReadToEndAsync();
        Process? child = null;
        try
        {
            string line = (await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
            line.ShouldStartWith("CHILD ");
            child = Process.GetProcessById(int.Parse(line[6..], System.Globalization.CultureInfo.InvariantCulture));
            (await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("READY");
            string records = Path.Combine(directory, "mounts");
            var record = MountReservation.Read(records, key)!.State;
            record.LaunchGated.ShouldBeTrue();
            record.ChildPid.ShouldBeNull();
            child.HasExited.ShouldBeFalse();
            owner.Kill();
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            child.HasExited.ShouldBeTrue();
            using var lease = MountProfileLease.Acquire(directory, key);
            (await MountRecovery.RecoverAsync(records, key, new MacMountRecoveryHost(), default)).ShouldBeTrue();
            MountReservation.Read(records, key).ShouldBeNull();
        }
        finally
        {
            if (!owner.HasExited)
            { owner.Kill(true); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            if (child is not null)
            {
                if (!child.HasExited)
                { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                child.Dispose();
            }
            await errors.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, true);
        }
    }

    [MacStateAclTests.MacAclFact]
    public async Task StartupOwnerCrash_RecordedUnmountedChild_IsRecoveredByNativeHost()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-startup-recovery-" + Guid.NewGuid().ToString("N"));
        string key = new('a', 64);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { typeof(TestHost.Program).Assembly.Location, directory, key, "reserve-starting" })
            start.ArgumentList.Add(arg);
        using var owner = Process.Start(start)!;
        Task<string> errors = owner.StandardError.ReadToEndAsync();
        Process? child = null;
        try
        {
            (await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("READY");
            string records = Path.Combine(directory, "mounts");
            var reservation = MountReservation.Read(records, key)!;
            reservation.State.MountIdentity.ShouldBeNull();
            child = Process.GetProcessById(reservation.State.ChildPid!.Value);
            child.HasExited.ShouldBeFalse();
            owner.Kill(); // Simulate supervisor loss while leaving the recorded child alive.
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            child.HasExited.ShouldBeFalse();
            using var lease = MountProfileLease.Acquire(directory, key);
            var host = new MacMountRecoveryHost();
            host.FindMount(reservation.State.Mountpoint).ShouldBeNull();
            (await MountRecovery.RecoverAsync(records, key, host, default)).ShouldBeTrue();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            child.HasExited.ShouldBeTrue();
            MountReservation.Read(records, key).ShouldBeNull();
            (await MountRecovery.RecoverAsync(records, key, host, default)).ShouldBeFalse();
        }
        finally
        {
            if (!owner.HasExited)
            { owner.Kill(true); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            if (child is not null)
            {
                if (!child.HasExited)
                { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                child.Dispose();
            }
            await errors.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, true);
        }
    }

    [TokenCommandTests.UnixFact]
    public async Task ProcessCrash_ReleasesLeaseButPreservesReservation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-reservation-" + Guid.NewGuid().ToString("N"));
        string key = new('c', 64);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { typeof(TestHost.Program).Assembly.Location, directory, key, "reserve" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("READY");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var lease = MountProfileLease.Acquire(directory, key);
            string records = Path.Combine(directory, "mounts");
            Should.Throw<MountPrerequisiteException>(() => MountReservation.EnsureAbsent(records, key));
            // Even a damaged record stays closed; it must never be interpreted as no mount.
            File.WriteAllText(Path.Combine(records, key + ".json"), "{");
            Should.Throw<MountPrerequisiteException>(() => MountReservation.EnsureAbsent(records, key));
            MountReservation.EnsureAbsent(records, new string('d', 64));
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(true); await process.WaitForExitAsync(); }
            await errors;
            Directory.Delete(directory, true);
        }
    }

    [TokenCommandTests.UnixFact]
    public void Reservation_RecordsProcessIdentityAndClearsOnlyExplicitly()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-reservation-" + Guid.NewGuid().ToString("N"));
        string key = new('e', 64);
        try
        {
            var reservation = MountReservation.Create(directory, key, "/synthetic/mount", "nfs-development", "/synthetic/rclone");
            using var child = Process.GetCurrentProcess();
            reservation.RecordChild(child);
            string path = Path.Combine(directory, key + ".json");
            using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            record.RootElement.GetProperty("ChildPid").GetInt32().ShouldBe(Environment.ProcessId);
            record.RootElement.GetProperty("ChildStartedUtc").GetDateTime().ShouldBe(child.StartTime.ToUniversalTime());
            Should.Throw<MountPrerequisiteException>(() => MountReservation.Create(directory, key,
                "/different/mount", "nfs-development", "/synthetic/rclone"));
            reservation.ClearAfterStop();
            MountReservation.EnsureAbsent(directory, key);
            Directory.GetFiles(directory).ShouldBeEmpty();
        }
        finally { Directory.Delete(directory, true); }
    }
}
