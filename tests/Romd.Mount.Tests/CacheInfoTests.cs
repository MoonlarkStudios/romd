using Romd.Mount.Cli;
using Romd.Mount.Credentials;
using Romd.Mount.Mounting;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class CacheInfoTests
{
    [TokenCommandTests.UnixFact]
    public void Read_GroupsRetainedGenerationsWithoutIncludingOtherProfiles()
    {
        string state = NewState();
        string key = new('a', 64), first = new('b', 64), current = new('c', 64);
        try
        {
            string parent = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
            string profile = PrivateStateDirectory.Ensure(Path.Combine(parent, key));
            string old = PrivateStateDirectory.Ensure(Path.Combine(profile, first));
            string active = PrivateStateDirectory.Ensure(Path.Combine(profile, current));
            File.WriteAllBytes(Path.Combine(old, "old-content"), new byte[100]);
            File.WriteAllBytes(Path.Combine(active, "current-content"), new byte[25]);
            string unrelated = PrivateStateDirectory.Ensure(Path.Combine(parent, new string('d', 64)));
            File.WriteAllBytes(Path.Combine(unrelated, "other-profile"), new byte[1000]);
            var selected = CacheInfo.Read(state, key, default, generation: current);
            selected.Path.ShouldBe(active);
            selected.LogicalBytes.ShouldBe(25);
            selected.Files.ShouldBe(1);
            var all = CacheInfo.Read(state, key, default);
            all.Path.ShouldBe(profile);
            all.LogicalBytes.ShouldBe(125);
            all.Files.ShouldBe(2);
            all.Directories.ShouldBe(2);
            Should.Throw<ArgumentException>(() => CacheInfo.Read(state, key, default, generation: "../outside"));
            string link = Path.Combine(profile, new string('e', 64));
            Directory.CreateSymbolicLink(link, unrelated);
            Should.Throw<IOException>(() => CacheInfo.Read(state, key, default, generation: new string('e', 64)));
            Should.Throw<IOException>(() => CacheInfo.Read(state, key, default));
            Directory.Delete(link);
        }
        finally { Directory.Delete(state, true); }
    }

    [MacStateAclTests.MacAclFact]
    public async Task Read_SparseAllocationMatchesNativeStatAndExcludesHoles()
    {
        string state = NewState();
        try
        {
            string parent = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
            string key = new('c', 64);
            string root = PrivateStateDirectory.Ensure(Path.Combine(parent, key));
            string file = Path.Combine(root, "sparse-é-content");
            const long logical = 128L * 1024 * 1024;
            using (var stream = File.Create(file))
            {
                stream.SetLength(logical);
                stream.Position = logical - 4096;
                stream.Write(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096));
                stream.Flush(true);
            }
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "-f", "%z %b", file })
                start.ArgumentList.Add(value);
            using var process = System.Diagnostics.Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            process.ExitCode.ShouldBe(0, await error);
            string[] numbers = (await output).Trim().Split(' ');
            long blocks = long.Parse(numbers[1], System.Globalization.CultureInfo.InvariantCulture);
            var info = CacheInfo.Read(state, key, default);
            info.LogicalBytes.ShouldBe(long.Parse(numbers[0], System.Globalization.CultureInfo.InvariantCulture));
            info.LogicalBytes.ShouldBe(logical);
            info.AllocatedFileBytes.ShouldBe(blocks * 512);
            info.AllocatedFileBytes!.Value.ShouldBeGreaterThan(0);
            info.AllocatedFileBytes.Value.ShouldBeLessThan(logical);
            info.Files.ShouldBe(1);
        }
        finally { Directory.Delete(state, true); }
    }

    [MacStateAclTests.MacAclFact]
    public void Allocation_RejectsMissingLinksAndSocketsWithoutOpeningThem()
    {
        // macOS Unix socket paths are limited to 104 bytes; its default TMPDIR
        // can consume most of that before the unique fixture name is appended.
        string state = PrivateStateDirectory.Ensure(Path.Combine("/tmp", "romd-alloc-" + Guid.NewGuid().ToString("N")));
        try
        {
            string missing = Path.Combine(state, "absent");
            Should.Throw<IOException>(() => MacFileAllocation.Read(missing));
            string link = Path.Combine(state, "link");
            File.CreateSymbolicLink(link, missing);
            Should.Throw<IOException>(() => MacFileAllocation.Read(link));
            string socketPath = Path.Combine(state, "socket");
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
            Should.Throw<IOException>(() => MacFileAllocation.Read(socketPath));
        }
        finally { Directory.Delete(state, true); }
    }

    [TokenCommandTests.UnixFact]
    public void Read_CountsOnlySelectedScopeAndPreservesFiles()
    {
        string state = NewState();
        string key = new('a', 64);
        try
        {
            CacheInfo.Read(state, key, default).LogicalBytes.ShouldBe(0);
            Directory.Exists(Path.Combine(state, "cache")).ShouldBeFalse();
            string parent = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
            string root = PrivateStateDirectory.Ensure(Path.Combine(parent, key));
            Directory.CreateDirectory(Path.Combine(root, "metadata"));
            File.WriteAllBytes(Path.Combine(root, "content"), new byte[41]);
            File.WriteAllBytes(Path.Combine(root, "metadata", "ranges"), new byte[7]);
            string other = PrivateStateDirectory.Ensure(Path.Combine(parent, new string('b', 64)));
            File.WriteAllBytes(Path.Combine(other, "unrelated"), new byte[100]);
            var info = CacheInfo.Read(state, key, default);
            info.LogicalBytes.ShouldBe(48);
            info.Files.ShouldBe(2);
            info.Directories.ShouldBe(1);
            new FileInfo(Path.Combine(root, "content")).Length.ShouldBe(41);
            Should.Throw<IOException>(() => CacheInfo.Read(state, key, default, maximumEntries: 1));
            Should.Throw<IOException>(() => CacheInfo.Read(state, key, default, maximumDepth: 0));
            Should.Throw<OperationCanceledException>(() => CacheInfo.Read(state, key, new CancellationToken(true)));
        }
        finally { Directory.Delete(state, true); }
    }

    [TokenCommandTests.UnixFact]
    public void Read_RejectsNestedAndDanglingRootLinks()
    {
        string state = NewState();
        string key = new('a', 64);
        try
        {
            string parent = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
            string root = Path.Combine(parent, key);
            Directory.CreateSymbolicLink(root, Path.Combine(state, "absent"));
            Should.Throw<IOException>(() => CacheInfo.Read(state, key, default));
            File.Delete(root); // Remove the dangling link itself, not its missing target.
            PrivateStateDirectory.Ensure(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "outside"), state);
            Should.Throw<IOException>(() => CacheInfo.Read(state, key, default));
            Directory.Delete(Path.Combine(root, "outside"));
        }
        finally { Directory.Delete(state, true); }
    }

    [MacStateAclTests.MacAclFact]
    public async Task Command_UsesSavedBindingOfflineAndHonorsLeaseAndReservation()
    {
        string state = NewState();
        var origin = new Uri("https://127.0.0.1:1");
        string profile = "cache-info-" + Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        using var client = new MountIdentityClient(origin);
        var scope = new MountCredentialScope(new BoundMountIdentity(client, instance), profile);
        MountReservation? reservation = null;
        try
        {
            await new ProfileBinding(state, origin, profile).SaveAsync(instance, default);
            async Task<(int Code, string Output)> Run(string command = "cache-info")
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                int code = await Program.RunAsync([command, "--server", origin.AbsoluteUri,
                    "--profile", profile, "--state-directory", state], output, error, default);
                return (code, output.ToString() + error.ToString());
            }
            var result = await Run();
            result.Code.ShouldBe(0, result.Output);
            result.Output.ShouldContain("Logical bytes (including metadata): 0");
            result.Output.ShouldContain("Allocated file bytes (including metadata): 0");
            result.Output.ShouldContain("not reclaimable space");
            result.Output.ShouldContain("not allocated disk space");
            (await Run("cache-clear")).Code.ShouldBe(0);
            (await Run("recover")).Code.ShouldBe(0);
            using (MountProfileLease.AcquireCredential(scope))
            {
                (await Run()).Code.ShouldBe(2);
                (await Run("cache-clear")).Code.ShouldBe(2);
                (await Run("recover")).Code.ShouldBe(2);
            }
            using (MountProfileLease.AcquireCredential(scope))
                reservation = MountReservation.Create(MountReservation.DirectoryPath, scope.Key,
                    "/synthetic/no-mount", "nfs-development", "/synthetic/no-child");
            var blocked = await Run();
            blocked.Code.ShouldBe(2);
            blocked.Output.ShouldContain("unresolved reservation");
            (await Run("cache-clear")).Code.ShouldBe(2);
            (await Run("recover")).Code.ShouldBe(2);
        }
        finally
        {
            reservation?.ClearAfterStop();
            File.Delete(Path.Combine(MountCacheNamespace.DirectoryPath, scope.Key + ".txt"));
            Directory.Delete(state, true);
            File.Delete(Path.Combine(MountProfileLease.CredentialLockDirectory, scope.Key + ".lock"));
        }
    }

    private static string NewState() => PrivateStateDirectory.Ensure(
        Path.Combine(Path.GetTempPath(), "romd-cache-info-" + Guid.NewGuid().ToString("N")));
}
