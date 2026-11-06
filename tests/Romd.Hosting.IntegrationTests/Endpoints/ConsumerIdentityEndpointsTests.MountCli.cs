using System.Diagnostics;
using System.Net;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Mount;
using Romd.Mount.Credentials;
using Shouldly;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using System.Security.Cryptography;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [MountCliKeychainFact]
    public Task MountCli_LoginRestartBusyProfileAndRevocation_UseRealKeychainAndServer() => RunMountCliAsync(false);

    [MountCliFilesystemFact]
    public Task MountCli_MountedReadsAndGracefulShutdown_UseSupervisedBroker() => RunMountCliAsync(true);

    [MountCliFilesystemFact]
    public Task MountCli_MountedExpiryAndServiceOutage_RecoverWithoutRemount() => RunMountCliAsync(true, resilience: true);

    [MountCliFilesystemFact]
    public Task MountCli_MountedSessionRevocation_DeniesUncachedReadAndUnmounts() => RunMountCliAsync(true, revokeMounted: true);

    [MountCliDiskFact]
    public Task MountCli_CacheVolumeFull_ReadFailsAndRecoversAfterSpaceIsFreed() => RunMountCliAsync(true, diskFull: true);

    [MountCliDiskFact]
    public Task MountCli_MinimumFreeSpace_EvictsClosedCacheBelowSizeTarget() => RunMountCliAsync(true, minimumFree: true);

    [MountCliFilesystemFact]
    public Task MountCli_SupervisorCrash_RecoversRecordedOrphanWithoutChangingCredentials() => RunMountCliAsync(true, crash: true);

    [PackagedMountFact]
    public Task MountCli_Packaged_LoginMountRecoveryAndLogout_UseBundledExecutables() => RunMountCliAsync(true, packaged: true);

    [MountCliCacheFact]
    public Task MountCli_OversizedCachedFile_RecordsRetentionAfterClose() => RunMountCliAsync(true, cacheEviction: true);

    [MountCliCacheFact]
    public Task MountCli_ConcurrentOversizedCacheFiles_RecordRepeatedEviction() => RunMountCliAsync(true, cacheEviction: true, cacheFiles: 4);

    [MountCliFskitFact]
    public Task MountCli_FskitPrivateHomeMount_ReadsEmulatorAndGracefulShutdown() => RunMountCliAsync(true, fskit: true);

    private static async Task RunMountCliAsync(bool mount, bool resilience = false, bool revokeMounted = false, bool diskFull = false, bool crash = false, bool packaged = false, bool cacheEviction = false, int cacheFiles = 1, bool minimumFree = false, bool fskit = false)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var revocationFailure = new RevocationFailureFilter();
        var transport = new MountTransportProbeFilter();
        await using var fixture = await ConsumerHostFixture.CreateAsync(
            services =>
            {
                services.AddSingleton<IStartupFilter>(revocationFailure);
                services.AddSingleton(new ConsumerVfsOptions { AllowDevelopmentBearer = true, Enabled = true });
                if (resilience || revokeMounted)
                    services.AddSingleton<IStartupFilter>(transport);
                if (resilience)
                {
                    services.AddOpenIddict().AddServer(options =>
                        options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler =>
                            handler.SetOrder(int.MinValue).UseInlineHandler(context =>
                            {
                                (context.Principal ?? throw new InvalidOperationException("Expected a sign-in principal."))
                                    .SetAccessTokenLifetime(TimeSpan.FromSeconds(8));
                                return ValueTask.CompletedTask;
                            })));
                    services.PostConfigure<OpenIddictValidationOptions>(options =>
                        options.TokenValidationParameters.ClockSkew = TimeSpan.Zero);
                }
            }, networkListener: true);
        int library = await fixture.CreateLibraryAsync("CLI mount library");
        await fixture.CreateConsumerUserAsync("cli-probe", "cli-probe@localhost", "Password123", user => user.LibraryId = library);
        await using var source = await VfsProbeContent.CreateAsync();
        if (cacheEviction)
            source.Length.ShouldBeGreaterThan(1024L * 1024 * 1024);
        if (diskFull || minimumFree)
        {
            source.Kind.ShouldBe("synthetic");
            source.Length.ShouldBe(4 * 1024 * 1024);
        }
        string relativePath = await fixture.SeedVfsProbeAsync(library, source);
        string[] cachePaths = cacheEviction && cacheFiles > 1
            ? await fixture.AddCacheProbeAliasesAsync(library, relativePath, cacheFiles - 1)
            : [relativePath];
        using var http = fixture.Factory.CreateClient();
        using var identity = new MountIdentityClient(http.BaseAddress!, allowLoopbackHttp: true);
        var bound = await identity.BindAsync();
        string profile = "cli-test-" + Guid.NewGuid().ToString("N");
        await using var volume = diskFull || minimumFree ? await VfsScratchVolume.CreateAsync() : null;
        string directory = volume?.StateDirectory ?? Path.Combine(Path.GetTempPath(), "romd-cli-test-" + Guid.NewGuid().ToString("N"));
        var scope = new MountCredentialScope(bound, profile);
        var store = new MacKeychainCredentialStore();
        string alternateDirectory = directory + "-alternate";
        // FSKit rejects temporary-directory mountpoints and its volumes ignore ownership,
        // so use a unique private root under the home directory, never the user's ~/.romd.
        string? fskitRoot = fskit
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".romd-it-" + Guid.NewGuid().ToString("N"))
            : null;
        string mountpoint = fskitRoot is null ? Path.Combine(directory, "mounted")
            : Path.Combine(fskitRoot, bound.InstanceId.ToString("D"), profile, "vfs");
        if (fskitRoot is not null)
            Directory.CreateDirectory(Path.GetDirectoryName(mountpoint)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process Start(string command, string? stateDirectory = null)
        {
            string? packagedCli = packaged ? Environment.GetEnvironmentVariable("ROMD_VFS_MOUNT_CLI") : null;
            var start = new ProcessStartInfo(packagedCli ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (packagedCli is null)
                start.ArgumentList.Add(typeof(Romd.Mount.Program).Assembly.Location);
            else
            {
                // Prove the published client does not discover the developer SDK.
                start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
                start.Environment["DOTNET_ROOT"] = Path.Combine(directory, "absent-dotnet-runtime");
                start.Environment.Remove("DOTNET_HOST_PATH");
            }
            foreach (string arg in new[] { command, "--server", http.BaseAddress!.AbsoluteUri,
                "--profile", profile, "--state-directory", stateDirectory ?? directory, "--allow-loopback-http" })
                start.ArgumentList.Add(arg);
            if (command == "mount")
            {
                foreach (string arg in new[] { "--mount-backend", fskit ? "macfuse-fskit" : "nfs-development", "--mountpoint", mountpoint })
                    start.ArgumentList.Add(arg);
                if (packagedCli is null)
                    foreach (string arg in new[] { "--rclone", Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")! })
                        start.ArgumentList.Add(arg);
                else
                    foreach (string arg in new[] { "--cache-size-mib", "8", "--cache-min-free-mib", "1" })
                        start.ArgumentList.Add(arg);
                if (minimumFree)
                    foreach (string arg in new[] { "--cache-size-mib", "1024", "--cache-min-free-mib", "32" })
                        start.ArgumentList.Add(arg);
            }
            return Process.Start(start)!;
        }
        async Task<(int Code, string Output)> Command(string command = "status", string? stateDirectory = null)
        {
            using var process = Start(command, stateDirectory);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            try
            { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(command == "recover" ? 60 : 30)); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            return (process.ExitCode, await output + await error);
        }
        async Task Login(string account)
        {
            using (var login = Start("login"))
            {
                Task<string> error = login.StandardError.ReadToEndAsync();
                try
                {
                    string? code = null;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (await login.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                    {
                        if (line.StartsWith("Device code: ", StringComparison.Ordinal))
                        { code = line[13..]; break; }
                    }
                    code.ShouldNotBeNullOrWhiteSpace();
                    using var approval = await http.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(http, "/connect/verify", new Dictionary<string, string>
                    {
                        ["user_code"] = code!,
                        ["login"] = account,
                        ["password"] = "Password123"
                    }));
                    approval.StatusCode.ShouldBe(HttpStatusCode.OK);
                    Task<string> rest = login.StandardOutput.ReadToEndAsync();
                    await login.WaitForExitAsync(deadline.Token);
                    login.ExitCode.ShouldBe(0, await error);
                    (await rest).ShouldContain("Credentials saved");
                }
                finally { if (!login.HasExited) { login.Kill(true); await login.WaitForExitAsync(); } }
            }
        }
        try
        {
            await Login("cli-probe");
            if (packaged)
            {
                // A distinct unsigned app identity owns this item. The legacy
                // Keychain backend must reject this cross-app read without UI.
                await Should.ThrowAsync<MountCredentialException>(() =>
                {
                    if (!OperatingSystem.IsMacOS())
                        throw new PlatformNotSupportedException();
                    return store.ReadAsync(scope).WaitAsync(TimeSpan.FromSeconds(5));
                });
                await ProbePackagedLifecycleAsync(Start, Command, source, relativePath, directory, scope.Key, revocationFailure);
                return;
            }
            var initialCache = await Command("cache-info");
            initialCache.Code.ShouldBe(0, initialCache.Output);
            string initialCachePath = initialCache.Output.Split('\n').First(line => line.StartsWith("Cache: ", StringComparison.Ordinal))[7..].Trim();
            string? initial = await store.ReadAsync(scope);
            initial.ShouldNotBeNull();
            Directory.CreateDirectory(alternateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(alternateDirectory, "profiles"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (string file in Directory.GetFiles(Path.Combine(directory, "profiles")))
                File.Copy(file, Path.Combine(alternateDirectory, "profiles", Path.GetFileName(file)));
            using (MountProfileLease.AcquireCredential(scope))
            {
                (await Command()).Code.ShouldBe(2);
                (await Command(stateDirectory: alternateDirectory)).Code.ShouldBe(2);
            }
            var valid = await Command(stateDirectory: alternateDirectory);
            valid.Code.ShouldBe(0, valid.Output);
            string? rotated = await store.ReadAsync(scope);
            (rotated != initial).ShouldBeTrue();
            valid.Output.Contains(rotated!, StringComparison.Ordinal).ShouldBeFalse();
            if (mount)
            {
                using var mounted = Start("mount");
                Task<string> errors = mounted.StandardError.ReadToEndAsync();
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(cacheEviction ? 420 : minimumFree ? 180 : resilience ? 120 : fskit ? 600 : 60));
                    bool ready = false;
                    while (await mounted.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                        if (line.StartsWith("Mounted read-only at ", StringComparison.Ordinal))
                        { ready = true; break; }
                    if (!ready)
                        throw new InvalidOperationException("Mount CLI did not report readiness: " + await errors);
                    int recordedChildPid;
                    string recordedBrokerDirectory;
                    using (var reservation = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
                        Path.Combine(MountProfileLease.CredentialLockDirectory, "mounts", scope.Key + ".json"))))
                    {
                        var record = reservation.RootElement;
                        record.GetProperty("Version").GetInt32().ShouldBe(2);
                        record.GetProperty("OwnerPid").GetInt32().ShouldBe(mounted.Id);
                        record.GetProperty("ChildPid").GetInt32().ShouldBeGreaterThan(0);
                        recordedChildPid = record.GetProperty("ChildPid").GetInt32();
                        recordedBrokerDirectory = record.GetProperty("BrokerDirectory").GetProperty("Directory").GetString()!;
                        Guid.Parse(record.GetProperty("BootSession").GetString()!).ShouldNotBe(Guid.Empty);
                        // FSKit never reports the kernel read-only flag; the supervisor's write probe proves it instead.
                        record.GetProperty("MountIdentity").GetProperty("ReadOnly").GetBoolean().ShouldBe(!fskit);
                        record.GetProperty("MountIdentity").GetProperty("FileSystem").GetString().ShouldBe(fskit ? "macfuse" : "nfs");
                    }
                    (await Command()).Code.ShouldBe(2);
                    (await Command("cache-clear")).Code.ShouldBe(2);
                    if (diskFull)
                    {
                        string mountedFile = Path.Combine(mountpoint, relativePath);
                        (await ReadMountedBlockAsync(mountedFile, 0, "before-disk-full")).ShouldBe(source.ReadRange(0, 4096));
                        try
                        {
                            volume!.Fill();
                            await ReadMountedBlockAsync(mountedFile, 2 * 1024 * 1024, "disk-full-uncached", expectDenied: true);
                            mounted.HasExited.ShouldBeFalse();
                        }
                        finally { volume!.ReleasePressure(); }
                        (await ReadMountedBlockAsync(mountedFile, 2 * 1024 * 1024, "disk-space-restored"))
                            .ShouldBe(source.ReadRange(2 * 1024 * 1024, 4096));
                        mounted.HasExited.ShouldBeFalse();
                    }
                    if (resilience)
                    {
                        string mountedFile = Path.Combine(mountpoint, relativePath);
                        string? beforeRenewal = await store.ReadAsync(scope);
                        int beforeUnauthorized = transport.Unauthorized;
                        await Task.Delay(TimeSpan.FromSeconds(12), timeout.Token);
                        (await ReadMountedBlockAsync(mountedFile, 0, "expired-token")).ShouldBe(source.ReadRange(0, 4096));
                        transport.Unauthorized.ShouldBeGreaterThan(beforeUnauthorized);
                        (await store.ReadAsync(scope) != beforeRenewal).ShouldBeTrue();

                        // Read far from the first chunk so a cache hit cannot hide the outage.
                        long offset = source.Length / 2 / 4096 * 4096;
                        int beforeRejected = transport.RejectedGets;
                        transport.Outage = true;
                        Task<byte[]> reading = ReadMountedBlockAsync(mountedFile, offset, "service-outage");
                        try
                        {
                            while (transport.RejectedGets == beforeRejected)
                                await Task.Delay(50, timeout.Token);
                            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                        }
                        finally { transport.Outage = false; }
                        (await reading).ShouldBe(source.ReadRange(offset, 4096));
                        transport.RejectedGets.ShouldBeGreaterThan(beforeRejected);

                        offset = source.Length * 3 / 4 / 4096 * 4096;
                        int beforeAborted = transport.AbortedGets;
                        transport.AbortConnections = true;
                        reading = ReadMountedBlockAsync(mountedFile, offset, "connection-interruption");
                        try
                        {
                            while (transport.AbortedGets == beforeAborted)
                                await Task.Delay(50, timeout.Token);
                            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                        }
                        finally { transport.AbortConnections = false; }
                        (await reading).ShouldBe(source.ReadRange(offset, 4096));
                        transport.AbortedGets.ShouldBeGreaterThan(beforeAborted);
                        mounted.HasExited.ShouldBeFalse();
                    }
                    if (revokeMounted)
                    {
                        source.Length.ShouldBeGreaterThanOrEqualTo(4 * 1024 * 1024);
                        string path = Path.Combine(mountpoint, relativePath);
                        (await ReadMountedBlockAsync(path, 0, "before-revocation")).ShouldBe(source.ReadRange(0, 4096));
                        string refresh = (await store.ReadAsync(scope))!;
                        await bound.RevokeAsync(refresh);
                        int beforeUnauthorized = transport.Unauthorized;
                        // The distant range has never been requested by this mount.
                        await ReadMountedBlockAsync(path, source.Length / 2 / 4096 * 4096,
                            "revoked-uncached", expectDenied: true);
                        transport.Unauthorized.ShouldBeGreaterThan(beforeUnauthorized);
                        await mounted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                        (await store.ReadAsync(scope)).ShouldBe(refresh);
                    }
                    else if (minimumFree)
                    {
                        await ProbeMinimumFreeSpaceAsync(Path.Combine(mountpoint, relativePath), initialCachePath, source, volume!, timeout.Token);
                    }
                    else if (cacheEviction)
                    {
                        if (cacheFiles == 1)
                            await ProbeCacheRetentionAsync(Path.Combine(mountpoint, relativePath), initialCachePath, source, timeout.Token);
                        else
                            await ProbeConcurrentCacheAsync(cachePaths.Select(path => Path.Combine(mountpoint, path)).ToArray(),
                                initialCachePath, source, timeout.Token);
                    }
                    else
                    {
                        // The emulator reads first so its traffic comes from the server through a cold mount.
                        if (fskit)
                            await ProbeFskitEmulatorAsync(Path.Combine(mountpoint, relativePath), directory, source);
                        long hashStarted = Stopwatch.GetTimestamp();
                        await using var file = File.OpenRead(Path.Combine(mountpoint, relativePath));
                        (await SHA256.HashDataAsync(file, timeout.Token)).ShouldBe(source.Sha256);
                        if (fskit)
                            Console.WriteLine($"FSKit full hash: {source.Length} bytes in {Stopwatch.GetElapsedTime(hashStarted).TotalSeconds:F1} s.");
                    }
                    if (crash)
                    {
                        using var orphan = Process.GetProcessById(recordedChildPid);
                        string? credential = await store.ReadAsync(scope);
                        mounted.Kill(entireProcessTree: false);
                        await mounted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        try
                        {
                            orphan.HasExited.ShouldBeFalse("The probe must exercise a surviving mount child.");
                            (await Command("cache-clear")).Code.ShouldBe(2);
                        }
                        finally
                        {
                            var recovery = await Command("recover");
                            recovery.Code.ShouldBe(0, recovery.Output);
                            recovery.Output.ShouldContain("Recovered the stopped mount");
                        }
                        await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        Directory.Exists(recordedBrokerDirectory).ShouldBeFalse();
                        (await store.ReadAsync(scope)).ShouldBe(credential);
                        var repeated = await Command("recover");
                        repeated.Code.ShouldBe(0, repeated.Output);
                        repeated.Output.ShouldContain("No unresolved mount reservation");
                        // Recovery intentionally preserves the mountpoint. Remove
                        // only this fixture's empty, now-unmounted directory.
                        Directory.Delete(mountpoint);
                    }
                }
                finally
                {
                    if (!mounted.HasExited)
                    {
                        var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                        signal.ArgumentList.Add("-TERM");
                        signal.ArgumentList.Add(mounted.Id.ToString());
                        using var stop = Process.Start(signal)!;
                        await stop.WaitForExitAsync();
                    }
                    await mounted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                if (crash)
                    mounted.ExitCode.ShouldNotBe(0);
                else
                    mounted.ExitCode.ShouldBe(revokeMounted ? 3 : 0, await errors);
                if (revokeMounted)
                    Console.WriteLine(await mounted.StandardOutput.ReadToEndAsync());
                Directory.Exists(mountpoint).ShouldBeFalse();
                rotated = await store.ReadAsync(scope);
                var cacheInfo = await Command("cache-info");
                cacheInfo.Code.ShouldBe(0, cacheInfo.Output);
                cacheInfo.Output.ShouldContain(Path.Combine(directory, "cache") + Path.DirectorySeparatorChar);
                cacheInfo.Output.ShouldContain(Path.Combine(directory, "cache", scope.Key) + Path.DirectorySeparatorChar);
                cacheInfo.Output.ShouldContain("Logical bytes (including metadata):");
                (await store.ReadAsync(scope)).ShouldBe(rotated);
            }
            revocationFailure.Enabled = true;
            (await Command("logout")).Code.ShouldBe(3);
            (await store.ReadAsync(scope) == rotated).ShouldBeTrue();
            revocationFailure.Enabled = false;
            (await Command("logout")).Code.ShouldBe(0);
            (await store.ReadAsync(scope)).ShouldBeNull();
            (await Should.ThrowAsync<MountIdentityException>(() => bound.RefreshAsync(rotated!)))
                .Failure.ShouldBe(IdentityFailure.SignInRequired);
            (await Command("logout")).Code.ShouldBe(0);
            var denied = await Command();
            denied.Code.ShouldBe(3);
            denied.Output.ShouldContain("SignInRequired");
            denied.Output.Contains(rotated!, StringComparison.Ordinal).ShouldBeFalse();
            // New account, same profile and shared Keychain scope. Old cache bytes
            // remain on disk but neither metadata root may select them again.
            Directory.CreateDirectory(Path.Combine(directory, "cache"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.GetDirectoryName(initialCachePath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(initialCachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string sentinel = Path.Combine(initialCachePath, "previous-account-sentinel");
            await File.WriteAllTextAsync(sentinel, "previous account bytes");
            int otherLibrary = await fixture.CreateLibraryAsync("Other account empty library");
            await fixture.CreateConsumerUserAsync("cli-other", "cli-other@localhost", "Password123",
                user => user.LibraryId = otherLibrary);
            await Login("cli-other");
            string? freshName = null;
            foreach (string state in new[] { directory, alternateDirectory })
            {
                var info = await Command("cache-info", state);
                info.Code.ShouldBe(0, info.Output);
                info.Output.ShouldContain("Files: 0; directories: 0");
                int retainedFiles = state == directory
                    ? Directory.EnumerateFiles(Path.GetDirectoryName(initialCachePath)!, "*", SearchOption.AllDirectories).Count()
                    : 0;
                info.Output.ShouldContain($"All retained profile caches in this state directory: {retainedFiles} files;");
                string selected = info.Output.Split('\n').First(line => line.StartsWith("Cache: ", StringComparison.Ordinal))[7..].Trim();
                Path.GetFileName(selected).ShouldNotBe(Path.GetFileName(initialCachePath));
                freshName ??= Path.GetFileName(selected);
                Path.GetFileName(selected).ShouldBe(freshName);
            }
            (await File.ReadAllTextAsync(sentinel)).ShouldBe("previous account bytes");
            if (mount && !resilience && !revokeMounted && !diskFull && !crash && !cacheEviction && !minimumFree)
            {
                // Prove an actual ROM-sized cache file survives the account change,
                // so denial cannot be explained by deleting the original cache.
                Directory.EnumerateFiles(initialCachePath, "*", SearchOption.AllDirectories)
                    .Any(file => new FileInfo(file).Length == source.Length).ShouldBeTrue();
                using var switched = Start("mount");
                Task<string> errors = switched.StandardError.ReadToEndAsync();
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    bool ready = false;
                    while (await switched.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                        if (line.StartsWith("Mounted read-only at ", StringComparison.Ordinal))
                        { ready = true; break; }
                    if (!ready)
                        throw new InvalidOperationException("Switched mount did not report readiness: " + await errors);
                    // This path and its first block were read and cached by the prior
                    // account, but are not in the new account's assigned library.
                    await ReadMountedBlockAsync(Path.Combine(mountpoint, relativePath), 0,
                        "switched-account-cached-path", expectDenied: true);
                    switched.HasExited.ShouldBeFalse();
                }
                finally
                {
                    if (!switched.HasExited)
                    {
                        var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                        signal.ArgumentList.Add("-TERM");
                        signal.ArgumentList.Add(switched.Id.ToString());
                        using var stop = Process.Start(signal)!;
                        await stop.WaitForExitAsync();
                    }
                    await switched.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                switched.ExitCode.ShouldBe(0, await errors);
                Directory.Exists(mountpoint).ShouldBeFalse();
                (await File.ReadAllTextAsync(sentinel)).ShouldBe("previous account bytes");
            }
            string? beforeCleanup = await store.ReadAsync(scope);
            var cleared = await Command("cache-clear");
            cleared.Code.ShouldBe(0, cleared.Output);
            cleared.Output.ShouldContain("Cache cleared:");
            File.Exists(sentinel).ShouldBeFalse();
            (await store.ReadAsync(scope)).ShouldBe(beforeCleanup);
            var empty = await Command("cache-info");
            empty.Code.ShouldBe(0, empty.Output);
            empty.Output.ShouldContain("All retained profile caches in this state directory: 0 files;");
            (await Command("logout")).Code.ShouldBe(0);
        }
        finally
        {
            if (mount && Directory.Exists(mountpoint))
                throw new IOException("Preserved CLI probe state because its mountpoint remains: " + directory);
            if (packaged)
            {
                var logout = await Command("logout");
                logout.Code.ShouldBe(0, logout.Output);
            }
            else
                await store.DeleteAsync(scope);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            if (Directory.Exists(alternateDirectory))
                Directory.Delete(alternateDirectory, true);
            // Remove only the empty private directories this probe created, deepest first.
            for (string? created = Path.GetDirectoryName(mountpoint); fskitRoot is not null && created is not null &&
                 created.StartsWith(fskitRoot, StringComparison.Ordinal); created = Path.GetDirectoryName(created))
                if (Directory.Exists(created))
                    Directory.Delete(created);
            File.Delete(Path.Combine(MountProfileLease.CredentialLockDirectory, "cache-namespaces", scope.Key + ".txt"));
            File.Delete(Path.Combine(MountProfileLease.CredentialLockDirectory, scope.Key + ".lock"));
        }
    }

    private static async Task ProbeFskitEmulatorAsync(string mountedFile, string directory, VfsProbeContent source)
    {
        string? dolphin = Environment.GetEnvironmentVariable("ROMD_VFS_DOLPHIN");
        if (string.IsNullOrEmpty(dolphin))
            return;
        if (source.Kind == "synthetic")
            throw new InvalidOperationException("Dolphin requires ROMD_VFS_PROBE_FILE.");
        Console.WriteLine("FSKit Dolphin probe starting on a cold mount.");
        object emulator = await VfsDolphinProbe.RunAsync(dolphin, mountedFile, Path.Combine(directory, "dolphin-user"));
        string json = System.Text.Json.JsonSerializer.Serialize(emulator, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        if (Environment.GetEnvironmentVariable("ROMD_MOUNT_FSKIT_REPORT") is { Length: > 0 } report)
            await File.WriteAllTextAsync(report, json);
        Console.WriteLine("FSKit Dolphin probe: " + json[..Math.Min(json.Length, 600)]);
    }

    private static async Task<byte[]> ReadMountedBlockAsync(string path, long offset, string phase, bool expectDenied = false)
    {
        var elapsed = Stopwatch.StartNew();
        // A child isolates potentially blocking native filesystem calls from the test runner.
        var start = new ProcessStartInfo("/bin/dd")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { "if=" + path, "bs=4096", "skip=" + (offset / 4096), "count=1" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var bytes = new MemoryStream();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
        finally
        {
            if (!process.HasExited)
            { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        await output;
        string diagnostic = await error;
        if (expectDenied)
        {
            process.ExitCode.ShouldNotBe(0, diagnostic);
            bytes.Length.ShouldBe(0);
        }
        else
            process.ExitCode.ShouldBe(0, diagnostic);
        Console.WriteLine($"Mounted recovery {phase}: {elapsed.Elapsed.TotalMilliseconds:F0} ms, {bytes.Length} bytes.");
        return bytes.ToArray();
    }

    private sealed class MountTransportProbeFilter : IStartupFilter
    {
        public volatile bool Outage;
        public volatile bool AbortConnections;
        public int Unauthorized;
        public int RejectedGets;
        public int AbortedGets;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path.StartsWithSegments("/vfs") && context.Request.Method == "GET" && AbortConnections)
                {
                    Interlocked.Increment(ref AbortedGets);
                    context.Abort();
                    return;
                }
                if (context.Request.Path.StartsWithSegments("/vfs") && context.Request.Method == "GET" && Outage)
                {
                    Interlocked.Increment(ref RejectedGets);
                    context.Response.StatusCode = 503;
                    return;
                }
                await continuation(context);
                if (context.Request.Path.StartsWithSegments("/vfs") && context.Response.StatusCode == 401)
                    Interlocked.Increment(ref Unauthorized);
            });
            next(app);
        };
    }

    private sealed class RevocationFailureFilter : IStartupFilter
    {
        public volatile bool Enabled;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (Enabled && context.Request.Path == "/connect/revocation")
                { context.Response.StatusCode = 503; return; }
                await continuation(context);
            });
            next(app);
        };
    }

    public sealed class MountCliKeychainFactAttribute : FactAttribute
    {
        public MountCliKeychainFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1")
                Skip = "Set ROMD_MOUNT_KEYCHAIN_TEST=1 on macOS for the real CLI/keychain probe.";
        }
    }

    public sealed class MountCliFilesystemFactAttribute : FactAttribute
    {
        public MountCliFilesystemFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1" ||
                Environment.GetEnvironmentVariable("ROMD_VFS_MOUNT") != "1" || !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")))
                Skip = "Requires macOS, ROMD_MOUNT_KEYCHAIN_TEST=1, ROMD_VFS_MOUNT=1, and ROMD_VFS_RCLONE.";
        }
    }

    public sealed class MountCliFskitFactAttribute : FactAttribute
    {
        public MountCliFskitFactAttribute()
        {
            if (!OperatingSystem.IsMacOSVersionAtLeast(26) || Environment.GetEnvironmentVariable("ROMD_MOUNT_FSKIT_TEST") != "1" ||
                Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1" || !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")))
                Skip = "Requires macOS 26+, enabled macFUSE FSKit, ROMD_MOUNT_FSKIT_TEST=1, ROMD_MOUNT_KEYCHAIN_TEST=1, and ROMD_VFS_RCLONE.";
        }
    }

    public sealed class MountCliDiskFactAttribute : FactAttribute
    {
        public MountCliDiskFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_DISK_TEST") != "1" ||
                Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1" ||
                Environment.GetEnvironmentVariable("ROMD_VFS_MOUNT") != "1" || !File.Exists(Environment.GetEnvironmentVariable("ROMD_VFS_RCLONE")))
                Skip = "Requires macOS, ROMD_MOUNT_DISK_TEST=1, ROMD_MOUNT_KEYCHAIN_TEST=1, ROMD_VFS_MOUNT=1, and ROMD_VFS_RCLONE.";
        }
    }
}
