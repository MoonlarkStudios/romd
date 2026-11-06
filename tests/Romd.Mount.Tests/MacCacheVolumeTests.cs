using System.Diagnostics;
using System.Xml.Linq;
using Romd.Mount.Cli;
using Romd.Mount.Credentials;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MacCacheVolumeTests
{
    [DiskImageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clear_MountedVolumeBoundary_PreservesMountedContent(bool nested)
    {
        // Canonical /private/tmp avoids mount-table ambiguity from /tmp symlinks.
        string state = PrivateStateDirectory.Ensure("/private/tmp/romd-cache-volume-" + Guid.NewGuid().ToString("N"));
        string key = new('a', 64);
        string parent = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
        string profile = PrivateStateDirectory.Ensure(Path.Combine(parent, key));
        string mountpoint = nested ? PrivateStateDirectory.Ensure(Path.Combine(profile, "generation")) : profile;
        string image = Path.Combine(state, "scratch.dmg");
        try
        {
            await Run("/usr/bin/hdiutil", "create", "-size", "64m", "-fs", "APFS", "-volname",
                "ROMDCacheProbe", "-type", "UDIF", image);
            await Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-mountpoint", mountpoint, image);
            (await IsMounted(mountpoint)).ShouldBeTrue();
            string sentinel = Path.Combine(mountpoint, "preserve.txt");
            await File.WriteAllTextAsync(sentinel, "mounted volume content");
            var failure = Should.Throw<IOException>(() => MacDirectoryCleanup.ClearCache(state, key, default));
            failure.Message.ShouldContain("filesystem boundaries");
            (await File.ReadAllTextAsync(sentinel)).ShouldBe("mounted volume content");
            (await IsMounted(mountpoint)).ShouldBeTrue();
            if (!nested)
                ExerciseDiskFullRecovery(mountpoint);
        }
        finally
        {
            // Check actual state even if attach failed or timed out. A directory
            // containing a live or unverified mount must never be deleted.
            if (await IsMounted(mountpoint))
                await Run("/usr/bin/hdiutil", "detach", mountpoint);
            if (await IsMounted(mountpoint))
                throw new IOException("Preserved scratch volume because its mount remains: " + state);
            var attached = XDocument.Parse(await Run("/usr/bin/hdiutil", "info", "-plist"));
            if (attached.Descendants("key").Any(element => element.Value == "image-path" &&
                element.ElementsAfterSelf().FirstOrDefault()?.Value == image))
                throw new IOException("Preserved scratch image because it remains attached: " + state);
            Directory.Delete(state, true);
        }
    }

    private static void ExerciseDiskFullRecovery(string volume)
    {
        string state = PrivateStateDirectory.Ensure(Path.Combine(volume, "private-state"));
        string registry = PrivateStateDirectory.Ensure(Path.Combine(state, "namespaces"));
        string key = new('c', 64);
        string original = MountCacheNamespace.GetOrCreateKey(registry, key);
        string cache = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
        string profile = PrivateStateDirectory.Ensure(Path.Combine(cache, key));
        string filler = Path.Combine(profile, "synthetic-fill.bin");
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024);
        IOException? full = null;
        using (var stream = new FileStream(filler, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, BufferSize = 1 }))
        {
            // Reduce write size to consume the tail as well, but never write more
            // than 128 MiB even if the fixture is accidentally misconfigured.
            foreach (int size in new[] { bytes.Length, 4096, 1 })
            {
                full = null;
                try
                {
                    while (stream.Length < 128L * 1024 * 1024)
                        stream.Write(bytes, 0, size);
                }
                catch (IOException error) { full = error; }
                full.ShouldNotBeNull("The isolated 64 MiB volume must reject writes before the safety cap.");
            }
        }
        Console.WriteLine($"Scratch volume exhausted: filler length {new FileInfo(filler).Length}, write error HResult {full!.HResult}.");
        Should.Throw<IOException>(() => MountCacheNamespace.Rotate(registry, key));
        MountCacheNamespace.GetOrCreateKey(registry, key).ShouldBe(original);
        MacDirectoryCleanup.ClearCache(state, key, default).ShouldBe(1);
        File.Exists(filler).ShouldBeFalse();
        string replacement = MountCacheNamespace.Rotate(registry, key);
        replacement.ShouldNotBe(original);
        MountCacheNamespace.GetOrCreateKey(registry, key).ShouldBe(replacement);
        File.WriteAllBytes(filler, bytes);
        MacDirectoryCleanup.ClearCache(state, key, default).ShouldBe(1);
    }

    private static async Task<bool> IsMounted(string path) =>
        (await Run("/sbin/mount")).Split('\n').Any(line => line.Contains(" on " + path + " (", StringComparison.Ordinal));

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        string diagnostic = await error.WaitAsync(TimeSpan.FromSeconds(10));
        if (process.ExitCode != 0)
            throw new IOException("Scratch-volume command failed: " + diagnostic);
        return await output.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public sealed class DiskImageTheoryAttribute : TheoryAttribute
    {
        public DiskImageTheoryAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_DISK_TEST") != "1")
                Skip = "Set ROMD_MOUNT_DISK_TEST=1 on macOS to create, mount, and detach isolated 64 MiB scratch disk images.";
        }
    }
}
