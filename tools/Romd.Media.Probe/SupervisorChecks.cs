using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;

namespace Romd.Media.Probe;

/// <summary>Exercises the production supervisor as container PID1, including Linux allocation enforcement.</summary>
internal static class SupervisorChecks
{
    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3 || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("Usage: --supervisor-probe <absolute configuration.json> <absolute new report.json>");
        var configuration = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(args[1], ct))
            ?? throw new InvalidDataException("Missing supervisor probe configuration.");
        await using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);
        Check.That(OperatingSystem.IsLinux() && Environment.ProcessId == 1 && GetEffectiveUserId() != 0,
            "non-root Linux container PID1");
        Check.That(new[] { configuration.ProductionWorker, configuration.FaultWorker, configuration.InputsDirectory,
            configuration.OracleIso }.All(Path.IsPathFullyQualified), "absolute qualification inputs");
        Check.That(configuration.ExitedLeaderRepetitions is >= 12 and <= 4096, "bounded exited-leader repetitions");
        const string imageWorker = "/app/media-worker/Romd.Media.Worker.dll";
        Check.That(Path.GetFullPath(configuration.ProductionWorker) == imageWorker, "untouched production image child path");
        string imageDirectory = Path.GetDirectoryName(imageWorker)!;
        string faultDirectory = Path.GetDirectoryName(configuration.FaultWorker)!;
        Check.That(Path.GetFileName(configuration.FaultWorker) == Path.GetFileName(imageWorker), "fault child executable name");
        var imageFiles = Directory.GetFiles(imageDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var faultFiles = Directory.GetFiles(faultDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Check.That(imageFiles.SequenceEqual(faultFiles) && !Directory.GetDirectories(faultDirectory).Any(), "fault clone complete flat package set");
        var packages = new List<object>();
        foreach (string file in imageFiles.Select(name => name!))
        {
            string imageHash = await FileHashAsync(Path.Combine(imageDirectory, file), ct);
            string faultHash = await FileHashAsync(Path.Combine(faultDirectory, file), ct);
            Check.That(file == Path.GetFileName(imageWorker) ? imageHash != faultHash : imageHash == faultHash,
                "only the emitted fault worker DLL differs: " + file);
            packages.Add(new { Name = file, ProductionSha256 = imageHash, FaultSha256 = faultHash });
        }
        var assemblies = new List<object>();
        foreach (var assembly in new[] { typeof(MediaWorkerSupervisor).Assembly, typeof(MediaInspectionService).Assembly,
            typeof(Romd.Storage.IContentAddressableStore).Assembly })
        {
            string imageFile = Path.Combine("/app", Path.GetFileName(assembly.Location));
            string loadedHash = await FileHashAsync(assembly.Location, ct);
            Check.That(loadedHash == await FileHashAsync(imageFile, ct), "loaded production image assembly " + assembly.GetName().Name);
            assemblies.Add(new { Name = assembly.GetName().Name, assembly.ManifestModule.ModuleVersionId, Sha256 = loadedHash });
        }
        var oracle = await Digests.FileAsync(configuration.OracleIso, ct);
        string scratch = Path.Combine(Path.GetDirectoryName(args[2])!, "supervisor-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var reports = new List<object>();
        var inputs = new List<ProbeInput>();
        bool passed = false;
        var options = new MediaProcessingOptions
        {
            WorkerPath = configuration.ProductionWorker,
            MaxConcurrent = 1,
            AdmissionTimeoutSeconds = 1,
            WallTimeoutSeconds = 3,
            CpuTimeoutSeconds = 3
        };
        var stagingOptions = Substitute.For<IRomdOptions>();
        stagingOptions.DataDirectory.Returns(scratch);
        stagingOptions.MaxUploadBytes.Returns(16L << 30);
        var runtime = new MediaProcessingRuntime(stagingOptions, options);
        await SaveAsync("in-progress", null, ct);
        try
        {
            var production = new CapturingSupervisor(new(options, runtime));
            var startup = await production.HandshakeAsync(ct);
            Check.That(startup.IsSuccess, "actual packaged production child handshake");
            reports.Add(new { Mode = "production-startup", Result = startup });
            Check.That(Prctl(37, out int subreaper, 0, 0, 0) == 0 && subreaper == 1, "production supervisor enables Linux subreaper");
            using (var verifier = new RvzVerifier(options, production, runtime))
                await InspectAsync(verifier, production, "valid", null, ct);

            options.WorkerPath = configuration.FaultWorker;
            var faults = new CapturingSupervisor(new(options, runtime));
            Check.That((await faults.HandshakeAsync(ct)).IsSuccess, "fault child exact packaged decoder and emitted worker identity");
            using (var verifier = new RvzVerifier(options, faults, runtime))
            {
                await InspectAsync(verifier, faults, "native-memory", null, ct);
                await InspectAsync(verifier, faults, "valid", null, ct);
                foreach (var (mode, failure) in new[] { ("oom", "LimitExceeded"), ("descendant-exit", "Crash"),
                    ("descendant-hang", "WallDeadline") })
                {
                    await InspectAsync(verifier, faults, mode, failure, ct);
                    await InspectAsync(verifier, faults, "valid", null, ct);
                }
                for (int attempt = 0; attempt < configuration.ExitedLeaderRepetitions; attempt++)
                    await InspectAsync(verifier, faults, "descendant-exit", "Crash", ct);
                await InspectAsync(verifier, faults, "valid", null, ct);
                await using var canceledSource = await InputAsync("descendant-hang", ct);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pending = verifier.VerifyAsync(canceledSource, cancellation.Token);
                using var cancellationSetup = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cancellationSetup.CancelAfter(TimeSpan.FromSeconds(10));
                bool canceled = false;
                try
                {
                    while (!File.Exists(canceledSource.Path + ".descendant.pid"))
                    {
                        Check.That(!pending.IsCompleted, "descendant starts before cancellation");
                        await Task.Delay(10, cancellationSetup.Token);
                    }
                    while (await ReadPidAsync(canceledSource.Path + ".pid", cancellationSetup.Token) is null ||
                        await ReadPidAsync(canceledSource.Path + ".descendant.pid", cancellationSetup.Token) is null)
                    {
                        Check.That(!pending.IsCompleted, "complete PID markers precede cancellation");
                        await Task.Delay(10, cancellationSetup.Token);
                    }
                    await cancellation.CancelAsync();
                    try
                    { await pending; }
                    catch (OperationCanceledException error)
                    {
                        Check.That(error.CancellationToken == cancellation.Token, "caller cancellation identity");
                        canceled = true;
                    }
                }
                finally
                {
                    await cancellation.CancelAsync();
                    try
                    { await pending; }
                    catch (OperationCanceledException) { }
                }
                Check.That(canceled, "caller cancellation remains cancellation");
                var canceledPids = await AssertStoppedAsync(canceledSource.Path, null, requireMarkers: true, requireDescendant: true, ct);
                reports.Add(new { Mode = "caller-cancellation", Outcome = "canceled", OwnedProcessesAbsent = true, Pids = canceledPids });
                await SaveAsync("in-progress", null, ct);
                RemoveWitnesses(canceledSource.Path);
                canceledSource.ConfirmStopped();
                await InspectAsync(verifier, faults, "valid", null, ct);
            }
            // This shortened CPU control keeps a longer wall bound. The reported
            // outcome combines host monitoring and kernel enforcement; it does not
            // attribute the stop exclusively to either mechanism.
            options.CpuTimeoutSeconds = 1;
            options.WallTimeoutSeconds = 10;
            var cpu = new CapturingSupervisor(new(options, runtime));
            var cpuStartup = await cpu.HandshakeAsync(ct);
            Check.That(cpuStartup.IsSuccess, "CPU control exact child startup and shortened actual limits");
            reports.Add(new { Mode = "cpu-startup", Result = cpuStartup, options.CpuTimeoutSeconds, options.WallTimeoutSeconds });
            using (var verifier = new RvzVerifier(options, cpu, runtime))
            {
                await InspectAsync(verifier, cpu, "cpu", "CpuDeadline", ct);
                await InspectAsync(verifier, cpu, "valid", null, ct);
            }
            options.CpuTimeoutSeconds = 3;
            options.WallTimeoutSeconds = 3;
            options.LinuxDataBytes = 2L << 30;
            var managedReject = new CapturingSupervisor(new(options, runtime));
            using (var verifier = new RvzVerifier(options, managedReject, runtime))
            {
                await InspectAsync(verifier, managedReject, "managed-reject", "LimitExceeded", ct);
                await InspectAsync(verifier, managedReject, "valid", null, ct);
            }
            options.MemoryBytes = 1L << 30;
            var managedControl = new CapturingSupervisor(new(options, runtime));
            using (var verifier = new RvzVerifier(options, managedControl, runtime))
                await InspectAsync(verifier, managedControl, "managed-control", null, ct);
            options.MemoryBytes = 512L << 20;
            var control = new CapturingSupervisor(new(options, runtime));
            using (var verifier = new RvzVerifier(options, control, runtime))
                await InspectAsync(verifier, control, "native-control", null, ct);
            options.WorkerPath = configuration.ProductionWorker;
            options.LinuxDataBytes = 1L << 30;
            var recoveredProduction = new CapturingSupervisor(new(options, runtime));
            using (var verifier = new RvzVerifier(options, recoveredProduction, runtime))
                await InspectAsync(verifier, recoveredProduction, "valid", null, ct);
            Check.That(inputs.All(input => input.ProcessStopped), "all source custody confirmed before scratch removal");
            Check.That(InspectionChecks.CleanupStaging(scratch), "no unresolved durable run or staging custody before scratch removal");
            await SaveAsync("passed", null, ct);
            passed = true;
        }
        catch (Exception error)
        {
            await PreserveFailureAsync(error, token => SaveAsync("failed", error.ToString(), token), CancellationToken.None);
        }
        finally
        {
            if (!passed && Directory.Exists(scratch))
                Console.WriteLine("Preserved failed qualification scratch: " + scratch);
        }

        async Task<ProbeInput> InputAsync(string mode, CancellationToken token)
        {
            string source = Path.Combine(configuration.InputsDirectory, mode + ".rvz");
            string path = Path.Combine(scratch, mode + ".rvz");
            File.Copy(source, path, overwrite: false);
            await using var stream = File.OpenRead(path);
            var input = new ProbeInput(path, stream.Length, Sha256.FromSpan(await SHA256.HashDataAsync(stream, token)));
            inputs.Add(input);
            return input;
        }

        async Task InspectAsync(RvzVerifier verifier, CapturingSupervisor supervisor, string mode, string? expectedFailure,
            CancellationToken token)
        {
            await using var source = await InputAsync(mode, token);
            long began = Stopwatch.GetTimestamp();
            var inspected = await verifier.VerifyAsync(source, token);
            if (expectedFailure is null)
            {
                Check.That(!inspected.IsError, mode + " inspected successfully");
                var proof = inspected.Value.Verification;
                Check.That(proof.ContentSha256.ToString() == oracle.Sha256 && proof.ContentSize == oracle.Size &&
                    proof.Sha1.ToString() == oracle.Sha1 && proof.Md5.ToString() == oracle.Md5 && proof.Crc32.ToString() == oracle.Crc,
                    mode + " all logical hashes match independent original");
                Check.That(proof.ArtifactSha256 == source.FileSha256 && proof.ArtifactSize == source.FileSize &&
                    proof.Profile == RvzVerifier.Profile, mode + " exact source and new evidence profile");
            }
            else
                Check.That(inspected.IsError && inspected.Errors.Count == 1 &&
                (inspected.FirstError.Code == "Rom.Media." + expectedFailure || mode == "cpu" && inspected.FirstError.Code == "Rom.Media.Crash"),
                mode + " permanent file failure; actual=" +
                (inspected.IsError ? inspected.FirstError.Code : "success") + "; execution=" + JsonSerializer.Serialize(supervisor.Last));
            var execution = supervisor.Last!;
            double elapsed = Stopwatch.GetElapsedTime(began).TotalSeconds;
            if (mode == "cpu")
                AssertCpuDeadline(execution, elapsed, options.CpuTimeoutSeconds, options.WallTimeoutSeconds);
            Check.That(execution.ProcessTreeStopped, mode + " supervisor confirmed containment");
            Check.That(execution.Execution is not null, mode + " bounded execution facts returned");
            Check.That(execution.Execution!.StandardOutputBytes <= MediaWorkerProtocol.MaximumOutputBytes &&
                execution.Execution.StandardErrorBytes <= 16384, mode + " bounded stdout and diagnostics");
            Check.That(!Directory.Exists("/proc/" + execution.Execution.ProcessId), mode + " child reaped before capacity reuse");
            Check.That(Kill(-execution.Execution.ProcessId, 0) != 0 && Marshal.GetLastPInvokeError() == 3,
                mode + " owned group no longer exists");
            string? native = null;
            string? managed = null;
            if (mode is "native-memory" or "native-control")
            {
                native = await File.ReadAllTextAsync(source.Path + ".native-probe", token);
                bool rejected = mode == "native-memory";
                Check.That(native == $"smallTouched=65536;requested=1140850688;largeRejected={rejected};data={options.LinuxDataBytes}",
                    mode + " actual native allocation control and OS rejection");
                Check.That(execution.Reply!.Identity.GcHeapLimitBytes == 512L << 20 &&
                    execution.Reply.Identity.LinuxLimits!.DataBytes == options.LinuxDataBytes, mode + " unchanged managed budget and actual OS budget");
            }
            if (mode is "managed-reject" or "managed-control")
            {
                managed = await File.ReadAllTextAsync(source.Path + ".managed-probe", token);
                Check.That(managed == $"requested=536870913;largeRejected={mode == "managed-reject"};gc={options.MemoryBytes}",
                    mode + " fixed managed allocation under identical OS budgets");
                Check.That(options.LinuxDataBytes == 2L << 30 && options.LinuxAddressSpaceBytes == 32L << 30,
                    mode + " unchanged OS allocation budgets");
            }
            var pids = await AssertStoppedAsync(source.Path, execution.Execution.ProcessId,
                requireMarkers: options.WorkerPath == configuration.FaultWorker,
                requireDescendant: mode is "descendant-exit" or "descendant-hang", token);
            reports.Add(new
            {
                Mode = mode,
                ExpectedFailure = expectedFailure,
                AcceptedFailures = mode == "cpu" ? new[] { "CpuDeadline", "Crash" } : expectedFailure is null ? Array.Empty<string>() : new[] { expectedFailure },
                Seconds = elapsed,
                CpuAttribution = mode == "cpu" ? "Combined supervisor/kernel enforcement; kernel-only attribution unqualified." : null,
                Result = execution,
                NativeAllocation = native,
                ManagedAllocation = managed,
                ProcessingLimits = new
                {
                    options.MemoryBytes,
                    options.LinuxDataBytes,
                    options.LinuxAddressSpaceBytes,
                    options.CpuTimeoutSeconds,
                    options.WallTimeoutSeconds
                },
                AllLogicalHashesMatched = expectedFailure is null,
                OwnedProcessesAbsent = true,
                Pids = pids,
                InspectionCapacity = 1
            });
            Console.WriteLine(mode + ": passed");
            await SaveAsync("in-progress", null, token);
            RemoveWitnesses(source.Path);
            source.ConfirmStopped();
        }

        async Task SaveAsync(string status, string? error, CancellationToken token)
        {
            output.Position = 0;
            output.SetLength(0);
            await JsonSerializer.SerializeAsync(output, new
            {
                Status = status,
                RecordedUtc = DateTimeOffset.UtcNow,
                Environment.ProcessId,
                UserId = GetEffectiveUserId(),
                Runtime = RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                Assemblies = assemblies,
                Packages = packages,
                Limits = options,
                OriginalOracle = oracle,
                Reports = reports,
                Error = error,
                Scratch = scratch,
                Scope = "Final-image supervisor/PID1, actual GC/native DATA allocation enforcement, combined CPU deadline control, owned process reap and single-slot recovery; no kernel-only CPU attribution, database or encoding claim."
            }, JsonOptions.Value, token);
            await output.FlushAsync(token);
        }
    }

    internal static void AssertCpuDeadline(MediaWorkerResult result, double elapsedSeconds, int cpuSeconds, int wallSeconds)
    {
        Check.That(cpuSeconds == 1 && wallSeconds == 10 && elapsedSeconds is > 0 and < 10 &&
            result.Failure is MediaWorkerFailure.CpuDeadline or MediaWorkerFailure.Crash &&
            result.Execution is { ExitCode: not null } && result.Execution.ExitCode != 0,
            "CPU burn fails permanently before the distinct wall bound with exact native exit facts");
        if (OperatingSystem.IsLinux() && result.Failure == MediaWorkerFailure.Crash)
            Check.That(result.Execution!.ExitCode is 137 or 152, "CPU burn crash is a kernel signal exit, not an ordinary child return");
        DefaultProcessingChecks.AssertStopped(result);
    }

    internal static async Task PreserveFailureAsync(Exception primary, Func<CancellationToken, Task> writeReport, CancellationToken ct)
    {
        try
        { await writeReport(ct); }
        catch (Exception secondary) { Console.Error.WriteLine("Qualification failure report could not be written: " + secondary.GetType().Name); }
        ExceptionDispatchInfo.Capture(primary).Throw();
    }

    internal static async Task<object[]> AssertStoppedAsync(string path, int? expectedLeader, bool requireMarkers,
        bool requireDescendant, CancellationToken ct)
    {
        var pids = new List<object>();
        int? leader = await ReadPidAsync(path + ".pid", ct);
        int? descendant = await ReadPidAsync(path + ".descendant.pid", ct);
        Check.That(!requireMarkers || leader is not null, "required complete PID marker .pid");
        Check.That(!requireDescendant || descendant is not null, "required complete PID marker .descendant.pid");
        foreach (string suffix in new[] { ".pid", ".descendant.pid" })
        {
            int? pid = suffix == ".pid" ? leader : descendant;
            if (pid is null)
                continue;
            Check.That(pid != Environment.ProcessId && !Directory.Exists("/proc/" + pid), "owned leader/descendant absent after return");
            if (suffix == ".pid")
            {
                Check.That(expectedLeader is null || pid == expectedLeader, "leader marker matches actual supervisor execution");
                Check.That(Kill(-pid.Value, 0) != 0 && Marshal.GetLastPInvokeError() == 3, "owned group no longer exists");
            }
            else
                Check.That(pid != leader, "descendant differs from leader");
            pids.Add(new { Marker = suffix, ProcessId = pid.Value, Absent = true });
        }
        return pids.ToArray();
    }

    private static void RemoveWitnesses(string path)
    {
        foreach (string suffix in new[] { ".pid", ".descendant.pid", ".native-probe", ".managed-probe" })
            File.Delete(path + suffix);
    }

    private static async Task<int?> ReadPidAsync(string path, CancellationToken ct) => File.Exists(path) &&
        int.TryParse(await File.ReadAllTextAsync(path, ct), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int pid) && pid > 1 ? pid : null;

    private static async Task<string> FileHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private sealed record Configuration(string ProductionWorker, string FaultWorker, string InputsDirectory, string OracleIso)
    {
        public int ExitedLeaderRepetitions { get; init; } = 12;
    }
    private sealed class ProbeInput(string path, long size, Sha256 hash) : ITempFile
    {
        public bool ProcessStopped { get; private set; }
        public void ConfirmStopped() => ProcessStopped = true;
        public bool TryBeginChildUse()
        {
            try
            {
                using var marker = new FileStream(Path + ".quarantine", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                marker.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        public bool TryEndChildUse()
        {
            try
            { File.Delete(Path + ".quarantine"); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        public bool Quarantine()
        {
            ProcessStopped = false;
            return File.Exists(Path + ".quarantine") || TryBeginChildUse();
        }
        public string Path => path;
        public long FileSize => size;
        public Sha256 FileSha256 => hash;
        public Stream OpenRead() => File.OpenRead(Path);
        public ValueTask DisposeAsync() { if (ProcessStopped) File.Delete(Path); return ValueTask.CompletedTask; }
    }
    private sealed class CapturingSupervisor(MediaWorkerSupervisor supervisor) : IMediaWorkerSupervisor
    {
        public MediaWorkerResult? Last { get; private set; }
        public bool IsAvailable => supervisor.IsAvailable;
        public async Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => Last = await supervisor.HandshakeAsync(ct);
        public async Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => Last = await supervisor.InspectAsync(source, ct);
        public async Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => Last = await supervisor.InspectIsoAsync(source, ct);
        public async Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes,
            Romd.Domain.Source.Rom.RomIsoDiscKind discKind,
            CancellationToken ct = default) => Last = await supervisor.EncodeAsync(source, output, logicalBytes, discKind, ct);
    }
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)] private static extern int Prctl(int option, out int value, ulong arg3, ulong arg4, ulong arg5);
}
