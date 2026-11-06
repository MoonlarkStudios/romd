using System.Text.Json;
using Romd.Mount.Cli;
using Romd.Mount.Credentials;
using Romd.Mount.Mounting;
using System.Runtime.InteropServices;

namespace Romd.Mount;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["_token"])
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                string token = await TokenCommandServer.ReadAsync(Environment.GetEnvironmentVariable("ROMD_MOUNT_TOKEN_SOCKET") ?? "",
                    Environment.GetEnvironmentVariable("ROMD_MOUNT_TOKEN_NONCE") ?? "", deadline.Token);
                await Console.Out.WriteLineAsync(token);
                return 0;
            }
            catch { await Console.Error.WriteLineAsync("Token command failed."); return 1; }
        }
        using var cancellation = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            context => { context.Cancel = true; cancellation.Cancel(); });
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await RunAsync(args, Console.Out, Console.Error, cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args.Length == 0 || args is ["--help"])
        {
            await output.WriteLineAsync("ROMD Mount (development)\nUsage: romd-mount <login|status|logout|mount|cache-info|cache-clear|recover> --server https://romd.example [--profile default] [--state-directory PATH] [--allow-loopback-http]\nmacOS Keychain is currently required. Mount requires --mount-backend <macfuse-fskit|nfs-development>, --rclone /absolute/path and --mountpoint /new/absolute/directory (for macfuse-fskit, inside an existing private 0700 directory such as ~/.romd/<server>/<profile>/vfs); Ctrl+C unmounts. Logout requires server access and preserves credentials if revocation fails. Recover verifies and cleans up a stopped NFS supervisor without changing credentials. Cache-info and cache-clear work offline for an unmounted saved profile; cache-clear deletes its retained cached downloads in this state directory.");
            await output.WriteLineAsync("Mount cache options: --cache-size-mib N (positive integer, default 1024); --cache-min-free-mib N (nonnegative integer, default 0/off). MiB = 1,048,576 bytes. These are per-mount eviction targets, not strict quotas; active files can exceed them. Settings apply only to this invocation and do not clear retained caches.");
            return 0;
        }
        try
        {
            var options = Parse(args);
            if (!OperatingSystem.IsMacOS())
            {
                await error.WriteLineAsync("Credential storage is currently supported on macOS only.");
                return 2;
            }
            PrivateStateDirectory.Ensure(options.StateDirectory);
            using var client = new MountIdentityClient(options.Origin, options.AllowLoopback);
            var binding = new ProfileBinding(options.StateDirectory, client.Origin, options.Profile);
            using var profileLease = MountProfileLease.Acquire(binding.LockDirectory, binding.Key);
            Guid? expected = await binding.ReadAsync(ct);
            if (options.Command != "login" && !expected.HasValue)
                throw new MountIdentityException(IdentityFailure.SignInRequired);
            // Offline cache inspection uses only the previously approved binding;
            // it must never discover a new instance or exchange credentials.
            var identity = options.Command is "cache-info" or "cache-clear" or "recover"
                ? new BoundMountIdentity(client, expected!.Value)
                : await client.BindAsync(expected, ct);
            var scope = new MountCredentialScope(identity, options.Profile);
            using var credentialLease = MountProfileLease.AcquireCredential(scope);
            if (options.Command == "recover")
            {
                bool recovered = await MountRecovery.RecoverAsync(MountReservation.DirectoryPath, scope.Key, new MacMountRecoveryHost(), ct);
                await output.WriteLineAsync(recovered
                    ? "Recovered the stopped mount. Cache, credentials, and mountpoint directory were preserved."
                    : "No unresolved mount reservation.");
                return 0;
            }
            MountReservation.EnsureAbsent(MountReservation.DirectoryPath, scope.Key);
            if (options.Command == "cache-clear")
            {
                await output.WriteLineAsync("Clearing this profile's retained caches in the selected state directory. Interrupted cleanup can leave partial results.");
                int removed = MacDirectoryCleanup.ClearCache(options.StateDirectory, scope.Key, ct);
                await output.WriteLineAsync($"Cache cleared: {removed} file/directory entries removed. Credentials and login namespace retained. Older unattributed flat caches are unchanged.");
                return 0;
            }
            if (options.Command == "cache-info")
            {
                string cacheKey = MountCacheNamespace.GetOrCreateKey(MountCacheNamespace.DirectoryPath, scope.Key);
                var cache = CacheInfo.Read(options.StateDirectory, scope.Key, ct, generation: cacheKey);
                var retained = CacheInfo.Read(options.StateDirectory, scope.Key, ct);
                await output.WriteLineAsync("Cache: " + cache.Path);
                await output.WriteLineAsync($"Files: {cache.Files}; directories: {cache.Directories}");
                await output.WriteLineAsync($"Logical bytes (including metadata): {cache.LogicalBytes}");
                await output.WriteLineAsync($"Allocated file bytes (including metadata): {cache.AllocatedFileBytes}");
                await output.WriteLineAsync($"All retained profile caches in this state directory: {retained.Files} files; logical bytes: {retained.LogicalBytes}; allocated file bytes: {retained.AllocatedFileBytes}");
                await output.WriteLineAsync("Logical bytes are not allocated disk space; sparse cache files may allocate less. No content was removed.");
                await output.WriteLineAsync("Allocation sums filesystem-reported file blocks per path; excludes directories and is not reclaimable space (clones, hard links, and snapshots may share blocks).");
                return 0;
            }
            var store = new MacKeychainCredentialStore();
            if (options.Command == "login")
            {
                // Bind the profile before credentials can be stored. Metadata contains no tokens.
                await binding.SaveAsync(identity.InstanceId, ct);
                var login = await identity.BeginLoginAsync(ct);
                await output.WriteLineAsync("Verification URL: " + login.VerificationUri.AbsoluteUri);
                await output.WriteLineAsync("Device code: " + login.UserCode);
                await output.FlushAsync(ct);
                var tokens = await WaitForLoginAsync(identity, login, ct);
                // Publish a new namespace before replacing shared credentials. If
                // either write fails, the old cache cannot accompany the new login.
                MountCacheNamespace.Rotate(MountCacheNamespace.DirectoryPath, scope.Key);
                await store.WriteAsync(scope, tokens.RefreshToken, ct);
                await output.WriteLineAsync("Signed in. Credentials saved in macOS Keychain.");
            }
            else if (options.Command == "logout")
            {
                string? refresh = await store.ReadAsync(scope, ct);
                if (refresh is not null)
                {
                    await identity.RevokeAsync(refresh, ct);
                    await store.DeleteAsync(scope, ct);
                }
                await output.WriteLineAsync("Signed out. Saved credential removed.");
            }
            else if (options.Command == "mount")
            {
                await MountSupervisor.RunAsync(options.Rclone!, options.Mountpoint!, options.StateDirectory,
                    identity, scope, store, output, ct, options.MountBackend!, options.Cache);
            }
            else
            {
                var session = new MountTokenSession(identity, scope, store);
                await session.GetAccessTokenAsync(ct: ct);
                await output.WriteLineAsync("Session valid. Server instance: " + identity.InstanceId.ToString("D"));
            }
            return 0;
        }
        catch (OperationCanceledException)
        { await error.WriteLineAsync("Operation canceled or device authorization expired."); return 130; }
        catch (MountIdentityException ex)
        { await error.WriteLineAsync("Authentication: " + ex.Failure + ". Sign in again if the session has ended; use a new local profile if the server instance changed."); return 3; }
        catch (MountCredentialException ex)
        { await error.WriteLineAsync("Keychain unavailable (OS status " + ex.Status + "). No plaintext fallback was used."); return 4; }
        catch (MountPrerequisiteException ex)
        { await error.WriteLineAsync(ex.Message); return 2; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or TimeoutException or OverflowException)
        { await error.WriteLineAsync("Invalid command, busy profile, or unavailable local state. Check --help, the state directory, and other running helpers."); return 2; }
    }

    internal static async Task<MountTokens> WaitForLoginAsync(BoundMountIdentity identity, DeviceLogin login, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(login.ExpiresInSeconds, 600)));
        int interval = login.PollIntervalSeconds;
        delay ??= Task.Delay;
        while (true)
        {
            await delay(TimeSpan.FromSeconds(interval), timeout.Token);
            try { return await identity.PollLoginAsync(login.DeviceCode, timeout.Token); }
            catch (MountIdentityException ex) when (ex.Failure == IdentityFailure.Pending) { }
            catch (MountIdentityException ex) when (ex.Failure == IdentityFailure.SlowDown)
            { interval = Math.Min(interval + 5, 600); }
        }
    }

    internal sealed record Options(string Command, Uri Origin, string Profile, string StateDirectory, bool AllowLoopback,
        string? Rclone, string? Mountpoint, string? MountBackend, MountCacheOptions Cache);
    internal static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("login" or "status" or "logout" or "mount" or "cache-info" or "cache-clear" or "recover")) throw new ArgumentException("Unknown command.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool loopback = false;
        for (int i = 1; i < args.Length; i++)
        {
            string name = args[i];
            if (name == "--allow-loopback-http")
            { if (loopback) throw new ArgumentException("Duplicate option."); loopback = true; continue; }
            if (name is not ("--server" or "--profile" or "--state-directory" or "--rclone" or "--mountpoint" or "--mount-backend" or "--cache-size-mib" or "--cache-min-free-mib") || ++i >= args.Length || !values.TryAdd(name, args[i]))
                throw new ArgumentException("Invalid option.");
        }
        if (!values.TryGetValue("--server", out string? server) || !Uri.TryCreate(server, UriKind.Absolute, out var origin))
            throw new ArgumentException("Server origin required.");
        string profile = values.GetValueOrDefault("--profile", "default");
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 128 || profile.Any(char.IsControl)) throw new ArgumentException("Invalid profile.");
        string directory = values.GetValueOrDefault("--state-directory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ROMD", "Mount"));
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("State directory must be absolute.");
        string? backend = values.GetValueOrDefault("--mount-backend");
        string? rclone = values.GetValueOrDefault("--rclone"), mountpoint = values.GetValueOrDefault("--mountpoint");
        if (args[0] == "mount")
        {
            if (backend is not ("nfs-development" or "macfuse-fskit")) throw new ArgumentException("Explicit mount backend required.");
            if (rclone is null || !Path.IsPathFullyQualified(rclone) || !File.Exists(rclone) ||
                mountpoint is null || !Path.IsPathFullyQualified(mountpoint)) throw new ArgumentException("Mount requires rclone and mountpoint paths.");
        }
        else if (rclone is not null || mountpoint is not null || backend is not null ||
            values.ContainsKey("--cache-size-mib") || values.ContainsKey("--cache-min-free-mib"))
            throw new ArgumentException("Mount options require mount command.");
        int CacheValue(string name, int fallback, int minimum)
        {
            if (!values.TryGetValue(name, out string? value)) return fallback;
            if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed < minimum)
                throw new MountPrerequisiteException($"{name} must be an integer from {minimum} to {int.MaxValue} MiB.");
            return parsed;
        }
        var cache = new MountCacheOptions(CacheValue("--cache-size-mib", 1024, 1), CacheValue("--cache-min-free-mib", 0, 0));
        return new(args[0], origin, profile, directory, loopback, rclone, mountpoint, backend, cache);
    }
}
