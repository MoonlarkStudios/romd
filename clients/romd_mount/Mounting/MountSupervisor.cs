using System.Diagnostics;
using Romd.Mount.Credentials;

namespace Romd.Mount.Mounting;

public static class MountSupervisor
{
    public static async Task RunAsync(string rclone, string mountpoint, string stateDirectory,
        BoundMountIdentity identity, MountCredentialScope scope, IMountCredentialStore store,
        TextWriter output, CancellationToken ct, string backend, MountCacheOptions? cacheOptions = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        cacheOptions ??= MountCacheOptions.Default;
        bool preparedMountpoint = backend == "macfuse-fskit" && Directory.Exists(mountpoint);
        mountpoint = CanonicalNewMountpoint(mountpoint, preparedMountpoint);
        ValidateBackend(backend, mountpoint);
        if (preparedMountpoint)
        {
            if (await IsMountedAsync(mountpoint) || Directory.EnumerateFileSystemEntries(mountpoint).Any())
                throw new MountPrerequisiteException("The prepared FSKit mountpoint must be empty and unmounted.");
            File.SetUnixFileMode(mountpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (backend == "nfs-development")
            await output.WriteLineAsync("Development NFS mount: other local users can access its loopback listener.");
        await output.WriteLineAsync($"Cache target: {cacheOptions.SizeMiB} MiB; minimum free space target: {cacheOptions.MinimumFreeMiB} MiB (0 disables it). Active files can exceed these targets.");
        var session = new MountTokenSession(identity, scope, store);
        var ended = new TaskCompletionSource<IdentityFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? previous = null;
        await using var tokens = new TokenCommandServer(async token =>
        {
            if (ended.Task.IsCompletedSuccessfully) throw new MountIdentityException(ended.Task.Result);
            try
            {
                string value = await session.GetAccessTokenAsync(previous, token);
                previous = value;
                return value;
            }
            catch (MountIdentityException ex) when (ex.Failure is IdentityFailure.SignInRequired or IdentityFailure.ServerChanged)
            {
                ended.TrySetResult(ex.Failure);
                throw;
            }
        });
        PrivateStateDirectory.Ensure(stateDirectory);
        string cacheRoot = PrivateStateDirectory.Ensure(Path.Combine(stateDirectory, "cache"));
        string profileCache = PrivateStateDirectory.Ensure(Path.Combine(cacheRoot, scope.Key));
        string cacheKey = MountCacheNamespace.GetOrCreateKey(MountCacheNamespace.DirectoryPath, scope.Key);
        string cache = PrivateStateDirectory.Ensure(Path.Combine(profileCache, cacheKey));
        ProcessStartInfo Start(params string[] args)
        {
            var start = new ProcessStartInfo(rclone) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
            foreach (string arg in args) start.ArgumentList.Add(arg);
            foreach (string arg in new[] { "--config", "/dev/null", "--retries", "1", "--low-level-retries", "3", "--timeout", "20s" }) start.ArgumentList.Add(arg);
            start.Environment["RCLONE_CONFIG_ROMD_TYPE"] = "webdav";
            start.Environment["RCLONE_CONFIG_ROMD_VENDOR"] = "other";
            start.Environment["RCLONE_CONFIG_ROMD_URL"] = new Uri(identity.Origin, "/vfs/library/").AbsoluteUri;
            start.Environment["RCLONE_CONFIG_ROMD_BEARER_TOKEN_COMMAND"] = TokenCommandServer.SelfCommand();
            tokens.Configure(start);
            return start;
        }

        if (await RunProcessAsync(Start("lsf", "romd:", "--max-depth", "1"), TimeSpan.FromSeconds(45), ct) != 0)
            throw new IOException("VFS authentication or listing failed.");
        using var process = new Process { StartInfo = Start(MountArguments(backend, mountpoint, cache, cacheOptions)) };
        GatedMountLaunch.Configure(process.StartInfo);
        Directory.CreateDirectory(mountpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        MountReservation reservation;
        try { reservation = MountReservation.Create(MountReservation.DirectoryPath, scope.Key, mountpoint, backend, rclone, tokens.RecoveryDirectory, launchGated: true); }
        catch { if (!preparedMountpoint) Directory.Delete(mountpoint); throw; }
        try { if (!process.Start()) throw new IOException("Cannot start the mount process."); }
        catch
        {
            if (!preparedMountpoint) Directory.Delete(mountpoint);
            reservation.ClearAfterStop();
            throw;
        }
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            reservation.RecordChild(process);
            await GatedMountLaunch.ReleaseAsync(process, ct);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readiness.CancelAfter(TimeSpan.FromSeconds(30));
            while (!await IsMountedAsync(mountpoint))
            {
                if (process.HasExited) throw new IOException("rclone exited before mounting.");
                await Task.Delay(100, readiness.Token);
            }
            reservation.RecordMounted();
            if (await RunProcessAsync(Utility("/bin/ls", "-A", mountpoint), TimeSpan.FromSeconds(30), readiness.Token) != 0)
                throw new IOException("Mounted directory read failed.");
            if (backend == "macfuse-fskit")
                await Task.Run(() => ProveReadOnly(mountpoint), readiness.Token).WaitAsync(readiness.Token);
            await output.WriteLineAsync("Mounted read-only at " + mountpoint + ". Press Ctrl+C to unmount.");
            await output.FlushAsync(ct);
            Task exited = process.WaitForExitAsync(ct);
            await Task.WhenAny(exited, ended.Task).WaitAsync(ct);
            if (ended.Task.IsCompletedSuccessfully)
                throw new MountIdentityException(ended.Task.Result);
            await exited;
            if (!ct.IsCancellationRequested) throw new IOException("The mount process exited unexpectedly.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (ended.Task.IsCompletedSuccessfully)
            {
                await output.WriteLineAsync("The mount session ended. Ejecting the read-only volume; active reads may fail.");
                await output.FlushAsync();
            }
            // Preserve the broker and both caller-owned leases until the mount is gone.
            while (true)
            {
                try { if (!await IsMountedAsync(mountpoint)) break; }
                catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    await output.WriteLineAsync("Mount state is unavailable; keeping supervision active until it can be checked.");
                    await output.FlushAsync();
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    continue;
                }
                int code;
                // A revoked session cannot complete its outstanding remote reads.
                // Eject only this supervisor's read-only volume in that terminal
                // case; ordinary cancellation still uses graceful unmount.
                var unmount = ended.Task.IsCompletedSuccessfully
                    ? Utility("/usr/sbin/diskutil", "unmount", "force", mountpoint)
                    : Utility("/sbin/umount", mountpoint);
                try { code = await RunProcessAsync(unmount, TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or System.ComponentModel.Win32Exception) { code = -1; }
                if (code == 0) break;
                await output.WriteLineAsync("Unmount is still pending. Close files using this mount; ownership and credentials remain supervised.");
                await output.FlushAsync();
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await Task.WhenAll(stdout, stderr);
            reservation.ClearAfterStop();
            // No recursive deletion: only remove the empty mountpoint we created.
            if (!preparedMountpoint) Directory.Delete(mountpoint);
            await output.WriteLineAsync("Unmounted. Cached bytes remain in " + cache + ".");
        }
    }

    internal static void ValidateBackend(string backend, string mountpoint)
    {
        if (backend == "nfs-development") return;
        if (backend != "macfuse-fskit") throw new ArgumentException("Unknown mount backend.");
        if (!OperatingSystem.IsMacOSVersionAtLeast(26))
            throw new MountPrerequisiteException("The FSKit adapter requires macOS 26 or later.");
        ValidateFskitMountpoint(mountpoint);
        if (!Directory.Exists("/Library/Filesystems/macfuse.fs"))
            throw new MountPrerequisiteException("Install macFUSE before selecting the FSKit adapter. See https://macfuse.github.io/; no fallback mount was started.");
    }

    // FSKit volumes report `noowners`: every local caller appears to own the files,
    // so the private parent directory is the only barrier to other local users.
    // /Volumes is world-traversable and therefore rejected.
    internal static void ValidateFskitMountpoint(string mountpoint)
    {
        try { PrivateStateDirectory.Verify(Path.GetDirectoryName(mountpoint) ?? throw new ArgumentException("Mountpoint has no parent.")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new MountPrerequisiteException("FSKit mountpoints must be inside an existing private directory you own " +
                "(mode 0700, no ACL, not a symbolic link), for example ~/.romd/<server>/<profile>/vfs. FSKit volumes ignore " +
                "file ownership, so that parent directory is what keeps other local users out.");
        }
    }

    private const int ReadOnlyFileSystemErrno = 30; // EROFS on Darwin and Linux.

    internal static bool IsReadOnlyRejection(Exception ex) => ex is IOException { HResult: ReadOnlyFileSystemErrno };

    // A create at the mount root must fail with EROFS; any accepted write is removed and rejected.
    internal static void ProveReadOnly(string mountpoint)
    {
        string probe = Path.Combine(mountpoint, ".romd-write-probe-" + Guid.NewGuid().ToString("N"));
        try { using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { } }
        catch (Exception ex) when (IsReadOnlyRejection(ex)) { return; }
        File.Delete(probe);
        throw new IOException("The mount accepted a write; refusing to continue with a writable volume.");
    }

    internal static string[] MountArguments(string backend, string mountpoint, string cache, MountCacheOptions? cacheOptions = null)
    {
        cacheOptions ??= MountCacheOptions.Default;
        string[] adapter = backend switch
        {
            "nfs-development" => ["nfsmount", "romd:", mountpoint, "--addr", "127.0.0.1:0", "--option",
                "ro,soft,intr,nolocks,noquota,dumbtimer,timeo=100,retrans=3,readahead=16"],
            // No local TCP export or allow-other/allow-root option. Actual FSKit
            // permission enforcement remains a required native validation gate.
            "macfuse-fskit" => ["mount", "romd:", mountpoint, "--option", "backend=fskit",
                "--dir-perms", "0700", "--file-perms", "0400"],
            _ => throw new ArgumentException("Unknown mount backend.")
        };
        // Over FSKit with no buffer, rclone's downloader restarted at 64 KiB after each read:
        // about 1 MiB/s behind a proxy admitting four concurrent GETs, versus about 53 MiB/s
        // with a 4 MiB buffer. Starting FSKit chunks at 1 MiB then cut Dolphin-trace transfer
        // from about 4.3x to 3.4x and halved requests. NFS keeps its measured settings.
        bool fskit = backend == "macfuse-fskit";
        return [.. adapter, "--read-only", "--vfs-cache-mode", "full",
            "--vfs-cache-max-size", cacheOptions.SizeMiB.ToString(System.Globalization.CultureInfo.InvariantCulture) + "M",
            "--vfs-cache-min-free-space", cacheOptions.MinimumFreeMiB.ToString(System.Globalization.CultureInfo.InvariantCulture) + "M",
            "--vfs-cache-max-age", "24h",
            "--vfs-read-ahead", "0", "--buffer-size", fskit ? "4M" : "0", "--dir-cache-time", "1s",
            "--vfs-read-chunk-size", fskit ? "1M" : "64k", "--vfs-read-chunk-size-limit", "4M", "--cache-dir", cache];
    }

    private static string CanonicalNewMountpoint(string path, bool allowPrepared = false)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || (!allowPrepared && Directory.Exists(path)) || File.Exists(path))
            throw new ArgumentException("Mountpoint must be a new absolute directory.");
        string full = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(parent)) throw new IOException("Mountpoint parent does not exist.");
        string canonical = Path.GetPathRoot(parent)!;
        foreach (string part in parent[canonical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(canonical, part));
            canonical = directory.LinkTarget is null ? directory.FullName : directory.ResolveLinkTarget(true)!.FullName;
        }
        string result = Path.Combine(canonical, Path.GetFileName(full));
        if (new DirectoryInfo(result).LinkTarget is not null) throw new IOException("Mountpoint must not be a symbolic link.");
        return result;
    }

    private static ProcessStartInfo Utility(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task<bool> IsMountedAsync(string mountpoint)
    {
        using var process = Process.Start(Utility("/sbin/mount"))!;
        Task<string> text = process.StandardOutput.ReadToEndAsync();
        Task drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw;
        }
        await drain;
        if (process.ExitCode != 0) throw new IOException("Cannot determine mount state.");
        return (await text).Split('\n').Any(line => line.Contains(" on " + mountpoint + " (", StringComparison.Ordinal));
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(start)!;
        Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); throw; }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }
}
