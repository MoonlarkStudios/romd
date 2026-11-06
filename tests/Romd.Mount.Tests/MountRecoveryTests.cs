using System.Text.Json;
using Romd.Mount.Mounting;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountRecoveryTests
{
    [TokenCommandTests.UnixFact]
    public async Task Recover_GatedUnrecordedChild_OnlyClearsWhenNoMountExists()
    {
        using var fixture = new Fixture();
        fixture.Write(fixture.Record with { LaunchGated = true, ChildPid = null, ChildStartedUtc = null, MountIdentity = null });
        await Should.ThrowAsync<MountPrerequisiteException>(() => MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default));
        fixture.Host.Actions.ShouldBeEmpty();
        File.Exists(fixture.Path).ShouldBeTrue();
        fixture.Host.Mount = null;
        (await MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default)).ShouldBeTrue();
        fixture.Host.Actions.ShouldBeEmpty();
        fixture.Host.ChildAlive.ShouldBeTrue(); // No guessed PID is signalled.
        File.Exists(fixture.Path).ShouldBeFalse();
    }

    [TokenCommandTests.UnixFact]
    public async Task Recover_VerifiedOrphan_UnmountsBeforeStoppingChildAndClearsLast()
    {
        using var fixture = new Fixture();
        fixture.Write(fixture.Record with { BrokerDirectory = new("/private/tmp/romd-token-" + Guid.NewGuid().ToString("N"), 1, 2, 3) });
        fixture.Host.BeforeStop = () => File.Exists(fixture.Path).ShouldBeTrue();
        (await MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default)).ShouldBeTrue();
        fixture.Host.Actions.ShouldBe(new[] { "unmount", "stop", "ipc" });
        File.Exists(fixture.Path).ShouldBeFalse();
        (await MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("live-owner")]
    [InlineData("different-mount")]
    [InlineData("pending-child")]
    [InlineData("pending-mount")]
    [InlineData("unsupported-backend")]
    [InlineData("different-boot-with-mount")]
    public async Task Recover_UnprovenOwnership_PreservesEverything(string scenario)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        switch (scenario)
        {
            case "live-owner":
                fixture.Host.OwnerAlive = true;
                break;
            case "different-mount":
                fixture.Host.Mount = fixture.Host.Mount! with { Id0 = 999 };
                break;
            case "pending-child":
                fixture.Write(fixture.Record with { ChildPid = null, ChildStartedUtc = null });
                break;
            case "pending-mount":
                fixture.Write(fixture.Record with { MountIdentity = null });
                break;
            case "unsupported-backend":
                fixture.Write(fixture.Record with { Backend = "macfuse-fskit" });
                break;
            case "different-boot-with-mount":
                fixture.Host.BootSession = Guid.NewGuid();
                break;
        }
        await Should.ThrowAsync<MountPrerequisiteException>(() => MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default));
        fixture.Host.Actions.ShouldBeEmpty();
        File.Exists(fixture.Path).ShouldBeTrue();
    }

    [TokenCommandTests.UnixFact]
    public async Task Recover_AfterRebootWithNoMount_DoesNotTouchReusedProcessIds()
    {
        using var fixture = new Fixture();
        fixture.Host.BootSession = Guid.NewGuid();
        fixture.Host.Mount = null;
        fixture.Host.OwnerAlive = true;
        (await MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default)).ShouldBeTrue();
        fixture.Host.Actions.ShouldBeEmpty();
        fixture.Host.ChildAlive.ShouldBeTrue();
    }

    [TokenCommandTests.UnixFact]
    public async Task Recover_RecordedChildBeforeMount_StopsChildAndClearsReservation()
    {
        using var fixture = new Fixture();
        fixture.Write(fixture.Record with { MountIdentity = null });
        fixture.Host.Mount = null;
        (await MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default)).ShouldBeTrue();
        fixture.Host.Actions.ShouldBe(new[] { "stop" });
        fixture.Host.ChildAlive.ShouldBeFalse();
        File.Exists(fixture.Path).ShouldBeFalse();
    }

    [TokenCommandTests.UnixFact]
    public async Task Recover_MountAppearsWhileStoppingStartupChild_PreservesReservation()
    {
        using var fixture = new Fixture();
        fixture.Write(fixture.Record with { MountIdentity = null });
        fixture.Host.Mount = null;
        fixture.Host.BeforeStop = () => fixture.Host.Mount = fixture.Record.MountIdentity;
        await Should.ThrowAsync<IOException>(() => MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default));
        fixture.Host.Actions.ShouldBe(new[] { "stop" });
        File.Exists(fixture.Path).ShouldBeTrue();
        fixture.Host.Mount.ShouldNotBeNull();
    }

    [TokenCommandTests.UnixFact]
    public async Task Recover_StartupWithoutChildIdentity_PreservesReservationEvenWhenUnmounted()
    {
        using var fixture = new Fixture();
        fixture.Write(fixture.Record with { MountIdentity = null, ChildPid = null, ChildStartedUtc = null });
        fixture.Host.Mount = null;
        await Should.ThrowAsync<MountPrerequisiteException>(() => MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default));
        fixture.Host.Actions.ShouldBeEmpty();
        File.Exists(fixture.Path).ShouldBeTrue();
    }

    [TokenCommandTests.UnixFact]
    public async Task Recover_ChildStopFailsOrRecordChanges_PreservesReservation()
    {
        using var fixture = new Fixture();
        fixture.Host.BeforeStop = () => throw new IOException("Cannot inspect child");
        await Should.ThrowAsync<IOException>(() => MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default));
        File.Exists(fixture.Path).ShouldBeTrue();
        fixture.Host.Mount.ShouldBeNull();
        fixture.Host.BeforeStop = () => fixture.Write(fixture.Record with { Executable = "/different/rclone" });
        await Should.ThrowAsync<IOException>(() => MountRecovery.RecoverAsync(fixture.Directory, fixture.Key, fixture.Host, default));
        File.Exists(fixture.Path).ShouldBeTrue();
    }

    [TokenCommandTests.UnixFact]
    public void Read_OldMalformedOversizedOrLinkedRecord_IsNotRecoverable()
    {
        using var fixture = new Fixture();
        fixture.Write(fixture.Record with { Version = 1 });
        Should.Throw<IOException>(() => MountReservation.Read(fixture.Directory, fixture.Key));
        fixture.Write(fixture.Record with { BrokerDirectory = new("/unrelated/directory", 1, 2, 3) });
        Should.Throw<IOException>(() => MountReservation.Read(fixture.Directory, fixture.Key));
        File.WriteAllText(fixture.Path, "{");
        Should.Throw<JsonException>(() => MountReservation.Read(fixture.Directory, fixture.Key));
        File.WriteAllText(fixture.Path, new string(' ', 16_385));
        Should.Throw<IOException>(() => MountReservation.Read(fixture.Directory, fixture.Key));
        File.Delete(fixture.Path);
        File.CreateSymbolicLink(fixture.Path, System.IO.Path.Combine(fixture.Directory, "missing"));
        Should.Throw<IOException>(() => MountReservation.Read(fixture.Directory, fixture.Key));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = PrivateStateDirectory.Ensure(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "romd-recover-" + Guid.NewGuid().ToString("N")));
        internal string Key { get; } = new('d', 64);
        internal string Path => System.IO.Path.Combine(Directory, Key + ".json");
        internal FakeHost Host { get; } = new();
        internal MountReservation.Record Record { get; }
        internal Fixture()
        {
            Record = new(2, "/synthetic/mount", "nfs-development", "/synthetic/rclone", 100,
                DateTime.UtcNow, 200, DateTime.UtcNow, Host.BootSession, Host.Mount);
            Write(Record);
        }
        internal void Write(MountReservation.Record record) => File.WriteAllText(Path, JsonSerializer.Serialize(record));
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class FakeHost : IMountRecoveryHost
    {
        public Guid BootSession { get; set; } = Guid.NewGuid();
        public MacMountIdentity? Mount { get; set; } = new(1, 2, 501, "nfs", "loopback:/", true);
        public bool OwnerAlive, ChildAlive = true;
        public Action? BeforeStop;
        public List<string> Actions { get; } = [];
        public void ClearBrokerDirectory(MacSocketDirectoryIdentity directory) => Actions.Add("ipc");
        public bool ProcessMatches(int pid, DateTime startedUtc) => pid == 100 ? OwnerAlive : ChildAlive;
        public MacMountIdentity? FindMount(string path) => Mount;
        public Task UnmountAsync(string path, MacMountIdentity expected, CancellationToken ct)
        { Actions.Add("unmount"); Mount = null; return Task.CompletedTask; }
        public Task StopChildAsync(int pid, DateTime startedUtc, string executable, CancellationToken ct, bool allowLauncher = false)
        { Actions.Add("stop"); BeforeStop?.Invoke(); ChildAlive = false; return Task.CompletedTask; }
    }
}
