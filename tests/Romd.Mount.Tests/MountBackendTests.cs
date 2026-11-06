using Romd.Mount.Mounting;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountBackendTests
{
    private static string[] CacheArguments(params string[] extra) => ["mount", "--server", "https://server.example",
        "--rclone", typeof(Program).Assembly.Location, "--mountpoint", Path.Combine(Path.GetTempPath(), "new-mount"),
        "--mount-backend", "nfs-development", .. extra];

    [Fact]
    public void CacheOptions_DefaultsAndCustomBudgetsReachBothAdapters()
    {
        var defaults = Program.Parse(CacheArguments()).Cache;
        defaults.SizeMiB.ShouldBe(1024);
        defaults.MinimumFreeMiB.ShouldBe(0);
        var custom = Program.Parse(CacheArguments("--cache-size-mib", "4096", "--cache-min-free-mib", "2048")).Cache;
        foreach (string backend in new[] { "nfs-development", "macfuse-fskit" })
        {
            string[] args = MountSupervisor.MountArguments(backend, "/Volumes/ROMD", "/private/cache", custom);
            args[Array.IndexOf(args, "--vfs-cache-max-size") + 1].ShouldBe("4096M");
            args[Array.IndexOf(args, "--vfs-cache-min-free-space") + 1].ShouldBe("2048M");
            args.ShouldContain("--read-only");
        }
    }

    [Theory]
    [InlineData("--cache-size-mib", "0")]
    [InlineData("--cache-size-mib", "-1")]
    [InlineData("--cache-min-free-mib", "-1")]
    [InlineData("--cache-size-mib", "1.5")]
    [InlineData("--cache-size-mib", "1,024")]
    [InlineData("--cache-size-mib", "2147483648")]
    [InlineData("--cache-min-free-mib", "secret-invalid-input")]
    public async Task CacheOptions_InvalidValueReturnsSafeGuidanceBeforeLocalState(string option, string value)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        (await Program.RunAsync(CacheArguments(option, value), output, error, default)).ShouldBe(2);
        error.ToString().ShouldContain(option + " must be an integer");
        error.ToString().ShouldNotContain("secret-invalid-input");
        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void CacheOptions_RejectDuplicateAndNonMountUse()
    {
        Should.Throw<ArgumentException>(() => Program.Parse(CacheArguments("--cache-size-mib", "8", "--cache-size-mib", "16")));
        Should.Throw<ArgumentException>(() => Program.Parse(["status", "--server", "https://server.example", "--cache-min-free-mib", "1"]));
        Should.Throw<ArgumentOutOfRangeException>(() => new MountCacheOptions(0, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new MountCacheOptions(1, -1));
    }

    [Fact]
    public void Mount_RequiresExplicitBackend_NoImplicitNfsFallback()
    {
        string executable = typeof(Program).Assembly.Location;
        string[] args = ["mount", "--server", "https://server.example", "--rclone", executable,
            "--mountpoint", Path.Combine(Path.GetTempPath(), "new-mount")];
        Should.Throw<ArgumentException>(() => Program.Parse(args));
        Should.Throw<ArgumentException>(() => Program.Parse([.. args, "--mount-backend", "automatic"]));
        Program.Parse([.. args, "--mount-backend", "macfuse-fskit"]).MountBackend.ShouldBe("macfuse-fskit");
        Program.Parse([.. args, "--mount-backend", "nfs-development"]).MountBackend.ShouldBe("nfs-development");
    }

    [Fact]
    public void Fskit_DoesNotExportNfsOrEnableOtherUsers()
    {
        string[] args = MountSupervisor.MountArguments("macfuse-fskit", "/Volumes/ROMD", "/private/cache");
        args[0].ShouldBe("mount");
        args.ShouldContain("backend=fskit");
        args.ShouldContain("--read-only");
        args.ShouldNotContain("--addr");
        args.ShouldNotContain("--allow-other");
        args.ShouldNotContain("--allow-root");
        args.ShouldContain("0700");
        args.ShouldContain("0400");
        Should.Throw<ArgumentException>(() => MountSupervisor.MountArguments("unknown", "/Volumes/ROMD", "/private/cache"));
    }

    [Fact]
    public void Fskit_UsesReadBufferAndLargerStartingChunk_NfsUnchanged()
    {
        string Value(string backend, string option)
        {
            string[] args = MountSupervisor.MountArguments(backend, "/private/mount", "/private/cache");
            return args[Array.IndexOf(args, option) + 1];
        }
        Value("macfuse-fskit", "--buffer-size").ShouldBe("4M");
        Value("macfuse-fskit", "--vfs-read-chunk-size").ShouldBe("1M");
        Value("nfs-development", "--buffer-size").ShouldBe("0");
        Value("nfs-development", "--vfs-read-chunk-size").ShouldBe("64k");
        foreach (string backend in new[] { "macfuse-fskit", "nfs-development" })
            Value(backend, "--vfs-read-chunk-size-limit").ShouldBe("4M");
    }

    [Fact]
    public void FskitMountpoint_RequiresPrivateParent_NotVolumes()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = Path.Combine(Path.GetTempPath(), "romd-fskit-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, PrivateStateDirectory.OwnerOnly);
        try
        {
            MountSupervisor.ValidateFskitMountpoint(Path.Combine(root, "vfs"));
            File.SetUnixFileMode(root, PrivateStateDirectory.OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Should.Throw<MountPrerequisiteException>(() => MountSupervisor.ValidateFskitMountpoint(Path.Combine(root, "vfs")))
                .Message.ShouldContain("private directory");
            Should.Throw<MountPrerequisiteException>(() => MountSupervisor.ValidateFskitMountpoint(Path.Combine(root, "absent", "vfs")));
            Should.Throw<MountPrerequisiteException>(() => MountSupervisor.ValidateFskitMountpoint("/Volumes/ROMD"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MountedIdentity_NfsRequiresKernelReadOnly_FskitRequiresMacfuse()
    {
        static MacMountIdentity Identity(string fileSystem, bool readOnly) => new(1, 2, 501, fileSystem, "source", readOnly);
        MountReservation.IsExpectedIdentity("nfs-development", Identity("nfs", true)).ShouldBeTrue();
        MountReservation.IsExpectedIdentity("nfs-development", Identity("nfs", false)).ShouldBeFalse();
        MountReservation.IsExpectedIdentity("nfs-development", Identity("macfuse", true)).ShouldBeFalse();
        // FSKit volumes never report MNT_RDONLY; the supervisor's write probe proves read-only instead.
        MountReservation.IsExpectedIdentity("macfuse-fskit", Identity("macfuse", false)).ShouldBeTrue();
        MountReservation.IsExpectedIdentity("macfuse-fskit", Identity("apfs", true)).ShouldBeFalse();
        MountReservation.IsExpectedIdentity("unknown", Identity("nfs", true)).ShouldBeFalse();
    }

    [Fact]
    public void ReadOnlyProbe_AcceptsOnlyReadOnlyFileSystemRejection()
    {
        if (OperatingSystem.IsWindows())
            return;
        string root = PrivateStateDirectory.Ensure(Path.Combine(Path.GetTempPath(), "romd-readonly-probe-" + Guid.NewGuid().ToString("N")));
        try
        {
            Should.Throw<IOException>(() => MountSupervisor.ProveReadOnly(root)).Message.ShouldContain("accepted a write");
            Directory.EnumerateFileSystemEntries(root).ShouldBeEmpty();
            MountSupervisor.IsReadOnlyRejection(new IOException("Read-only file system", 30)).ShouldBeTrue();
            MountSupervisor.IsReadOnlyRejection(new IOException("Input/output error", 5)).ShouldBeFalse();
            MountSupervisor.IsReadOnlyRejection(new UnauthorizedAccessException()).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NonMountCommand_RejectsBackendOption()
    {
        Should.Throw<ArgumentException>(() => Program.Parse(["login", "--server", "https://server.example",
            "--mount-backend", "nfs-development"]));
    }
}
