using Romd.Domain.Source.Rom;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Storage.Files;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Rvz;

namespace Romd.Infrastructure.Storage;

internal enum MediaWorkerSetupPoint { Started, Draining, Spawned, Identified, Permitted }

/// <summary>Owns a fresh child, bounded output and its Unix session through kill, reap and drain.</summary>
public sealed partial class MediaWorkerSupervisor : IMediaWorkerSupervisor
{
    private const int MaximumDiagnosticBytes = 16384;
    private readonly MediaProcessingOptions _options;
    private readonly Func<MediaWorkerProcessLifetime> _lifetimeFactory;
    private readonly ILogger<MediaWorkerSupervisor>? _logger;
    private readonly Func<Process, MediaWorkerSetupPoint, CancellationToken, Task>? _setup;
    private readonly ChdNativeCandidate? _chdCandidate;
    internal MediaProcessingRuntime Runtime { get; }
    private volatile bool _available;
    public bool IsAvailable => _available;
    private volatile bool _chdAvailable;
    public bool IsChdAvailable => _chdAvailable;
    /// <summary>Test-local compatibility constructor. Production supplies the shared runtime explicitly.</summary>
    public MediaWorkerSupervisor(MediaProcessingOptions options, ILogger<MediaWorkerSupervisor>? logger = null)
        : this(options, MediaProcessingRuntime.TestLocal(options), logger) { }
    public MediaWorkerSupervisor(MediaProcessingOptions options, MediaProcessingRuntime runtime, ILogger<MediaWorkerSupervisor>? logger = null)
        : this(options, () => new MediaWorkerProcessLifetime(), logger, runtime: runtime) { }
    /// <summary>Isolated experimental CHD opt-in; does not initialize native code in the parent.</summary>
    public MediaWorkerSupervisor(MediaProcessingOptions options, ChdNativeCandidate candidate, ILogger<MediaWorkerSupervisor>? logger = null)
        : this(options, () => new MediaWorkerProcessLifetime(), logger, chdCandidate: candidate) { }
    public MediaWorkerSupervisor(MediaProcessingOptions options, MediaProcessingRuntime runtime, ChdNativeCandidate candidate,
        ILogger<MediaWorkerSupervisor>? logger = null)
        : this(options, () => new MediaWorkerProcessLifetime(), logger, runtime: runtime, chdCandidate: candidate) { }
    internal MediaWorkerSupervisor(MediaProcessingOptions options, Func<MediaWorkerProcessLifetime> lifetimeFactory,
        ILogger<MediaWorkerSupervisor>? logger = null,
        Func<Process, MediaWorkerSetupPoint, CancellationToken, Task>? setup = null,
        MediaProcessingRuntime? runtime = null, ChdNativeCandidate? chdCandidate = null)
    {
        _options = options.ValidatedCopy();
        _lifetimeFactory = lifetimeFactory;
        _logger = logger;
        _setup = setup;
        _chdCandidate = chdCandidate;
        Runtime = runtime ?? MediaProcessingRuntime.TestLocal(options);
    }
    public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => RunAsync("handshake", null, ct);
    public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => RunAsync("inspect-rvz", source, ct);
    public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => RunAsync("inspect-iso", source, ct);
    public Task<MediaWorkerResult> HandshakeChdAsync(CancellationToken ct = default) => RunChdAsync("handshake-chd", null, ct);
    public Task<MediaWorkerResult> InspectChdAsync(ITempFile source, CancellationToken ct = default) => RunChdAsync("inspect-chd", source, ct);
    public Task<MediaWorkerResult> InspectChdCdAsync(ITempFile source, CancellationToken ct = default) => RunChdAsync("inspect-chd-cd", source, ct);
    public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default) =>
        RunAsync("encode", source, ct, output, logicalBytes, discKind);

    /// <summary>Fixed canonical DVD recipe candidate; raw facts are not recipe or deployment qualification, and
    /// separate child inspection must establish logical integrity.</summary>
    public Task<MediaWorkerResult> EncodeChdAsync(ITempFile source, IWritableTempFile output,
        ChdmanCandidate candidate, CancellationToken ct = default) =>
        RunAsync("encode-chd", source, ct, output, source.FileSize, toolCandidate: candidate);

    /// <summary>Measured ISO extraction only; caller must bind the prior verified CHD logical quartet before sealing.</summary>
    public Task<MediaWorkerResult> ExtractChdDvdAsync(ITempFile source, IWritableTempFile output,
        ChdmanCandidate candidate, long logicalBytes, CancellationToken ct = default) =>
        RunAsync("extract-chd-dvd", source, ct, output, logicalBytes, toolCandidate: candidate);

    private async Task<MediaWorkerResult> RunChdAsync(string operation, ITempFile? source, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested)
                throw new MediaWorkerStoppedCancellationException(ct);
            var result = _chdCandidate is null ? MediaWorkerResult.Failed(MediaWorkerFailure.Startup) : await RunAsync(operation, source, ct);
            if (!result.IsSuccess)
                _chdAvailable = false;
            return result;
        }
        catch { _chdAvailable = false; throw; }
    }

    private async Task<MediaWorkerResult> RunAsync(string operation, ITempFile? source, CancellationToken ct,
        IWritableTempFile? destination = null, long logicalBytes = 0, RomIsoDiscKind? discKind = null,
        ChdmanCandidate? toolCandidate = null, CdJobContext? cdJob = null)
    {
        bool chdEncoding = operation is "encode-chd" or "encode-chd-cd";
        int cpuSeconds = chdEncoding ? _options.ChdEncodeCpuTimeoutSeconds : _options.CpuTimeoutSeconds;
        int wallSeconds = chdEncoding ? _options.ChdEncodeWallTimeoutSeconds : _options.WallTimeoutSeconds;
        bool chdExtraction = operation == "extract-chd-dvd";
        bool chdTransform = chdEncoding || chdExtraction;
        bool chd = operation is "handshake-chd" or "inspect-chd" or "inspect-chd-cd" or "verify-chd-cd" or "reverify-chd-cd";
        void ClearAvailability()
        { if (chd) _chdAvailable = false; else _available = false; }
        MediaWorkerResult StartupFailure()
        { ClearAvailability(); return MediaWorkerResult.Failed(MediaWorkerFailure.Startup); }
        if (ct.IsCancellationRequested)
            throw new MediaWorkerStoppedCancellationException(ct);
        string expectedBuild;
        try
        {
            if (!File.Exists(_options.DotnetPath) || OperatingSystem.IsLinux() &&
                (!File.Exists("/usr/bin/prlimit") || (operation is "encode" or "encode-chd" or "extract-chd-dvd" or "encode-chd-cd") && !File.Exists("/usr/bin/nice")))
                return StartupFailure();
            using var binary = File.OpenRead(_options.WorkerPath);
            if (binary.Length is <= 0 or > (16L << 20))
                return StartupFailure();
            using var pe = new PEReader(binary);
            if (!pe.HasMetadata)
                return StartupFailure();
            var metadata = pe.GetMetadataReader();
            expectedBuild = "romd-media-worker/mvid:" + metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        { return StartupFailure(); }
        if (source is not null && (!Path.IsPathFullyQualified(source.Path) || source.FileSize < (operation is "inspect-iso" or "inspect-chd" or "inspect-chd-cd" ? 0 : 4) || source.FileSize > _options.MaxArtifactBytes))
            return MediaWorkerResult.Failed(MediaWorkerFailure.LimitExceeded);

        if (operation == "encode" && (source is null || destination is null || !Path.IsPathFullyQualified(destination.Path) ||
            string.Equals(source.Path, destination.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            logicalBytes <= 0x80 || logicalBytes > _options.MaxLogicalBytes || destination.MaximumBytes < 0x124 || destination.MaximumBytes > _options.MaxArtifactBytes))
            return MediaWorkerResult.Failed(MediaWorkerFailure.LimitExceeded);

        var rvzFamily = RvzDiscFamily.For(discKind)?.Codec;
        if (operation == "encode" && rvzFamily is null)
            return MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported);

        if (operation == "encode-chd" && (source is null || destination is null || toolCandidate is null ||
            !Path.IsPathFullyQualified(destination.Path) ||
            string.Equals(source.Path, destination.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            source.FileSize > _options.MaxLogicalBytes || destination.MaximumBytes < 124 || destination.MaximumBytes > _options.MaxArtifactBytes))
            return MediaWorkerResult.Failed(MediaWorkerFailure.LimitExceeded);

        if (chdExtraction && (source is null || destination is null || toolCandidate is null || source.FileSize < 124 ||
            source.FileSize > ChdDvdLimits.MaximumArtifactBytes || source.FileSha256.IsEmpty || !Path.IsPathFullyQualified(destination.Path) ||
            string.Equals(source.Path, destination.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            logicalBytes is < 2048 or > ChdDvdLimits.MaximumLogicalBytes || logicalBytes % 2048 != 0 || logicalBytes > _options.MaxLogicalBytes ||
            destination.MaximumBytes < logicalBytes || destination.MaximumBytes > _options.MaxArtifactBytes))
            return MediaWorkerResult.Failed(MediaWorkerFailure.LimitExceeded);

        ITempFile[] inputs = source is null ? [] : [source, .. cdJob?.AdditionalSources ?? []];
        if (cdJob is not null && (inputs.Any(input => !Path.IsPathFullyQualified(input.Path) || input.FileSha256.IsEmpty || input.FileSize < 1 ||
                input.FileSize > _options.MaxArtifactBytes || destination is not null && input.Path == destination.Path) ||
            inputs.Select(input => input.Path).Distinct(StringComparer.Ordinal).Count() != inputs.Length ||
            cdJob.Request.FileSize > ChdCdRequest.MaximumBytes || operation == "encode-chd-cd" &&
                (destination is null || toolCandidate is null || destination.MaximumBytes is < 124 or > ChdCdLimits.MaximumArtifactBytes)))
            return MediaWorkerResult.Failed(MediaWorkerFailure.LimitExceeded);

        using var acquired = MediaProcessingRuntime.Current is null ? await Runtime.AcquireAsync(ct: ct) : null;
        var run = MediaProcessingRuntime.Current ?? acquired!;
        using var activeRun = run.Activate();

        MediaWorkerProcessLifetime lifetime;
        try
        { lifetime = _lifetimeFactory(); }
        catch (Exception error) when (error is Win32Exception or IOException) { return StartupFailure(); }
        using var ownedLifetime = lifetime;
        var start = CreateStart(operation, source, destination, logicalBytes, discKind, toolCandidate, cdJob);
        if (lifetime.JobName is { } job)
        {
            start.ArgumentList.Add("--job");
            start.ArgumentList.Add(job);
        }
        foreach (string argument in new[] { "--run", run.RecordPath, "--nonce", run.Nonce })
            start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(wallSeconds));
        using var stopMonitoring = new CancellationTokenSource();
        using var stopDraining = new CancellationTokenSource();
        using var stopWaiting = new CancellationTokenSource();
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<OutputCapture> output = Task.FromResult(new OutputCapture([], false, 0, false));
        Task<OutputCapture> diagnostics = Task.FromResult(new OutputCapture([], false, 0, false));
        Task? exited = null;
        Task<CpuObservation> cpu = Task.FromResult(CpuObservation.Stopped);
        MediaWorkerFailure? failure = null;
        bool canceled = false;
        bool processTreeStopped = false;
        int processId = 0;
        try
        { run.Prepare(operation, expectedBuild, lifetime.JobName, source, destination, cdJob?.AdditionalSources); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return MediaWorkerResult.Failed(MediaWorkerFailure.SourceCustody); }
        List<ITempFile> begun = [];
        bool destinationBegun = false;
        foreach (var input in inputs)
        {
            if (!input.TryBeginChildUse())
            { EndPrelaunchCustody(); return MediaWorkerResult.Failed(MediaWorkerFailure.SourceCustody); }
            begun.Add(input);
        }
        if (destination is not null && !destination.TryBeginChildUse())
        { EndPrelaunchCustody(); return MediaWorkerResult.Failed(MediaWorkerFailure.SourceCustody); }
        destinationBegun = destination is not null;
        bool EndPrelaunchCustody()
        {
            bool released = true;
            foreach (var input in begun)
                if (!input.TryEndChildUse())
                { input.Quarantine(); released = false; }
            if (destinationBegun && destination is not null && !destination.TryEndChildUse())
            { destination.Quarantine(); released = false; }
            return released;
        }
        MediaChildProcess process;
        try
        {
            run.MarkLaunching();
            process = MediaChildProcess.Start(start, run.NativeLease);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            run.ConfirmStopped(); // Native Start proves no unjoined direct child on a failed launch.
            return EndPrelaunchCustody() ? StartupFailure() : MediaWorkerResult.Failed(MediaWorkerFailure.SourceCustody);
        }
        using var ownedProcess = process;
        try
        {
            processId = process.Id;
            if (_setup is not null)
                await _setup(process.Observer, MediaWorkerSetupPoint.Spawned, ct);
            await run.PermitAsync(process.Ready, process.Input, process.Id, deadline.Token, async () =>
            {
                if (_setup is not null)
                    await _setup(process.Observer, MediaWorkerSetupPoint.Identified, ct);
            });
            if (_setup is not null)
                await _setup(process.Observer, MediaWorkerSetupPoint.Permitted, ct);
            if (_setup is not null)
                await _setup(process.Observer, MediaWorkerSetupPoint.Started, ct);
            output = DrainAsync(process.Output, cdJob is not null ? MediaWorkerProtocol.MaximumCdJobOutputBytes : operation == "inspect-chd-cd" ? MediaWorkerProtocol.MaximumCdOutputBytes :
                MediaWorkerProtocol.MaximumOutputBytes, overflow, stopDraining.Token);
            diagnostics = DrainAsync(process.Error, MaximumDiagnosticBytes, overflow, stopDraining.Token);
            if (_setup is not null)
                await _setup(process.Observer, MediaWorkerSetupPoint.Draining, ct);
            exited = process.WaitAsync(stopWaiting.Token);
            cpu = ObserveNativeCpuAsync(process, stopMonitoring.Token, cpuLimitSeconds: cpuSeconds);
            Task wall = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
            Task first = await Task.WhenAny(exited, overflow.Task, cpu, wall);
            if (first == overflow.Task)
                failure = MediaWorkerFailure.OutputLimit;
            else if (first == wall)
            { canceled = ct.IsCancellationRequested; failure = MediaWorkerFailure.WallDeadline; }
            else if (first == cpu)
            {
                var observation = await cpu;
                if (observation != CpuObservation.Stopped)
                    failure = observation == CpuObservation.Deadline ? MediaWorkerFailure.CpuDeadline : MediaWorkerFailure.IoFailure;
                else
                    await exited.WaitAsync(deadline.Token);
            }
            else
                await exited.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) { canceled = ct.IsCancellationRequested; failure = MediaWorkerFailure.WallDeadline; }
        catch (InvalidDataException) { failure = MediaWorkerFailure.Protocol; }
        catch (Exception) { failure = MediaWorkerFailure.IoFailure; }
        finally
        {
            // Unix group termination is unconditional: the parent may have exited
            // while a descendant still owns a pipe or consumes resources.
            bool terminationConfirmed = true;
            try
            { process.Terminate(lifetime); }
            catch (Exception) { terminationConfirmed = false; }
            try
            {
                exited ??= process.WaitAsync(stopWaiting.Token);
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)) != exited)
                    await stopWaiting.CancelAsync();
                await exited;
                if (exited.IsCompletedSuccessfully && terminationConfirmed)
                {
                    await lifetime.JoinDescendantsAsync(processId, CancellationToken.None);
                    processTreeStopped = true;
                }
            }
            catch (Exception) { processTreeStopped = false; }
            finally
            {
                await stopMonitoring.CancelAsync();
                try
                { await cpu; }
                catch (OperationCanceledException) { }
                finally
                {
                    if (!processTreeStopped)
                    {
                        await stopDraining.CancelAsync();
                        process.Output.Dispose();
                        process.Error.Dispose();
                    }
                    try
                    { await Task.WhenAll(output, diagnostics); }
                    finally
                    {
                        if (!processTreeStopped)
                            run.Retain();
                        else
                        {
                            try
                            { run.ConfirmStopped(); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                            { run.Retain(); failure = MediaWorkerFailure.SourceCustody; }
                        }
                        foreach (var input in begun)
                        {
                            if (!processTreeStopped)
                                input.Quarantine();
                            else if (!input.TryEndChildUse())
                            {
                                input.Quarantine();
                                failure = MediaWorkerFailure.SourceCustody;
                                ClearAvailability();
                            }
                        }
                        if (destination is not null)
                        {
                            if (!processTreeStopped)
                                destination.Quarantine();
                            else if (!destination.TryEndChildUse())
                            {
                                destination.Quarantine();
                                failure = MediaWorkerFailure.SourceCustody;
                                ClearAvailability();
                            }
                        }
                    }
                }
            }
        }
        if (canceled && processTreeStopped)
            throw new MediaWorkerStoppedCancellationException(ct);
        var execution = new MediaWorkerExecution(processId, exited?.IsCompletedSuccessfully == true ? process.ExitCode : null, output.Result.TotalBytes,
            diagnostics.Result.TotalBytes, DiagnosticKind(diagnostics.Result.Bytes));
        MediaWorkerResult Finish(MediaWorkerResult result)
        {
            if (!result.IsSuccess)
            {
                if (operation == "handshake")
                    _available = false;
                if (chd)
                    _chdAvailable = false;
                _logger?.LogWarning("Media child {ChildProcessId} failed with {Failure}; exit {ExitCode}, stdout {OutputBytes}, stderr {DiagnosticBytes}, diagnostic {DiagnosticKind}",
                    execution.ProcessId, result.Failure, execution.ExitCode, execution.StandardOutputBytes,
                    execution.StandardErrorBytes, execution.DiagnosticKind);
            }
            return result with { Execution = execution };
        }
        if (!processTreeStopped)
        {
            ClearAvailability();
            return Finish(new(null, MediaWorkerFailure.Containment, ProcessTreeStopped: false));
        }
        if (output.Result.ReadFailure || diagnostics.Result.ReadFailure)
            failure = MediaWorkerFailure.IoFailure;
        if (output.Result.Overflow || diagnostics.Result.Overflow)
            failure = MediaWorkerFailure.OutputLimit;
        if (failure is { } failed)
            return Finish(MediaWorkerResult.Failed(failed));
        if (process.ExitCode != 0)
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Crash));
        if (!MediaWorkerProtocol.TryRead(output.Result.Bytes, out var reply) || reply is null)
            return Finish(operation is "handshake" or "handshake-chd" ? StartupFailure() : MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (
            reply.Identity.ProcessId != process.Id || (!OperatingSystem.IsWindows() && reply.Identity.SessionId != process.Id) ||
            reply.Identity.DecoderBuild != MediaInspectionService.DecoderBuild || reply.Identity.WorkerBuild != expectedBuild ||
            reply.Identity.GcHeapLimitBytes != _options.MemoryBytes || reply.Identity.JobName != lifetime.JobName || !ValidLinuxLimits(reply.Identity.LinuxLimits, cpuSeconds))
            return Finish(operation is "handshake" or "handshake-chd" ? StartupFailure() : MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (cdJob is not null)
            return Finish(ValidateCdJobResult(operation, reply, cdJob, source!, destination, toolCandidate, process.Id, cpuSeconds));
        if (reply.CdEncoding is not null || reply.CdVerification is not null || reply.CdFailure is not null)
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (!chd && (reply.NativeIdentity is not null || reply.ChdInspection is not null || reply.ChdRejection is not null))
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (reply.CdInspection is not null && operation != "inspect-chd-cd" ||
            operation == "inspect-chd-cd" && (reply.Inspection is not null || reply.ChdInspection is not null))
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (!chdTransform && (reply.ChdEncoding is not null || reply.ChdExtraction is not null || reply.ToolIdentity is not null ||
            reply.ChdEncodingRejection is not null || reply.ToolExecution is not null))
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (reply.Ps2Inspection is not null && operation is not ("inspect-iso" or "inspect-chd"))
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (chdTransform)
        {
            if (reply.Inspection is not null || reply.IsoInspection is not null || reply.Encoding is not null ||
                chdExtraction && reply.ChdEncoding is not null || chdEncoding && reply.ChdExtraction is not null ||
                reply.ToolIdentity is { } tool && !toolCandidate!.Matches(tool) ||
                reply.ChdEncodingRejection is { } rejection && !ValidChdEncodingRejectionPath(rejection, source!, destination!, toolCandidate!) ||
                reply.Status is MediaWorkerStatus.Ready or MediaWorkerStatus.Inspected ||
                reply.Status != MediaWorkerStatus.Encoded && (reply.ChdEncoding is not null || reply.ChdExtraction is not null || reply.ChdEncodingRejection is null))
                return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
            if (reply.ChdEncodingRejection?.Cause == ChdDvdEncodingCause.CpuBudgetExceeded)
            {
                if (reply.ToolExecution is not { } measured || measured.CpuMicroseconds < cpuSeconds * 1_000_000L - 20_000)
                    return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
                return Finish(new(reply, MediaWorkerFailure.CpuDeadline));
            }
            if (reply.Status == MediaWorkerStatus.Encoded && chdEncoding)
            {
                if (reply.ChdEncoding is not { } artifact || reply.ToolIdentity is null || reply.ToolExecution is not { } toolExecution ||
                    reply.ChdEncodingRejection is not null ||
                    artifact.SourceBytes != source!.FileSize || artifact.SourceSha256 != source.FileSha256.ToString() ||
                    artifact.LogicalBytes != source.FileSize || artifact.Header.LogicalBytes != (ulong)source.FileSize ||
                    artifact.ArtifactBytes > destination!.MaximumBytes || artifact.Header.ArtifactBytes != artifact.ArtifactBytes ||
                    artifact.RecipeVersion != ChdDvdCanonicalRecipe.Id || artifact.EncodingProfile != ChdDvdCanonicalRecipe.EncoderProfile ||
                    artifact.PriorityKind != "unix-nice" || artifact.PriorityValue is < 10 or > 19 ||
                    toolExecution.ProcessId <= 0 || toolExecution.ProcessId == process.Id || toolExecution.ExitCode != 0 ||
                    toolExecution.StandardOutputBytes is < 0 or > (256L << 10) ||
                    toolExecution.StandardErrorBytes < 0 || toolExecution.StandardErrorBytes > (256L << 10) - toolExecution.StandardOutputBytes)
                    return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
            }
            if (reply.Status == MediaWorkerStatus.Encoded && chdExtraction)
            {
                if (reply.ChdExtraction is not { } extracted || reply.ToolIdentity is null || reply.ToolExecution is not { } toolExecution ||
                    reply.ChdEncodingRejection is not null || extracted.SourceBytes != source!.FileSize ||
                    extracted.SourceSha256 != source.FileSha256.ToString() || extracted.LogicalBytes != logicalBytes ||
                    extracted.LogicalBytes > destination!.MaximumBytes || extracted.Profile != ChdDvdEncodingService.ExtractionProfile ||
                    extracted.PriorityKind != "unix-nice" || extracted.PriorityValue is < 10 or > 19 ||
                    toolExecution.ProcessId <= 0 || toolExecution.ProcessId == process.Id || toolExecution.ExitCode != 0 ||
                    toolExecution.StandardOutputBytes is < 0 or > (256L << 10) || toolExecution.StandardErrorBytes < 0 ||
                    toolExecution.StandardErrorBytes > (256L << 10) - toolExecution.StandardOutputBytes)
                    return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
            }
        }
        if (chd)
        {
            if (reply.IsoInspection is not null || reply.Encoding is not null ||
                reply.NativeIdentity is { } native && !_chdCandidate!.Matches(native) ||
                reply.ChdRejection is { } rejected && !ValidChdRejectionPath(rejected, source) ||
                operation == "handshake-chd" && (reply.Inspection is not null || reply.ChdInspection is not null ||
                    reply.ChdRejection is { Stage: not (ChdRejectionStage.NativeVerification or ChdRejectionStage.NativeLoad) }) ||
                (operation is "inspect-chd" or "inspect-chd-cd") && reply.Status == MediaWorkerStatus.Ready)
                return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
            if (operation == "handshake-chd" && reply.Status == MediaWorkerStatus.Ready)
            {
                if (reply.NativeIdentity is null || reply.ChdRejection is not null)
                    return Finish(StartupFailure());
                _chdAvailable = true;
                return Finish(new(reply, null));
            }
            if (reply.Status == MediaWorkerStatus.Inspected && (reply.NativeIdentity is null ||
                    (operation == "inspect-chd-cd" ? reply.CdInspection is null : reply.ChdInspection is null)) ||
                reply.Status != MediaWorkerStatus.Inspected && reply.ChdRejection is null)
                return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        }
        if (operation == "handshake")
        {
            if (reply.Status != MediaWorkerStatus.Ready)
                return Finish(StartupFailure());
            _available = true;
            return Finish(new(reply, null));
        }
        if (operation == "encode" && (reply.Status is MediaWorkerStatus.Ready or MediaWorkerStatus.Inspected) ||
            operation is not ("encode" or "encode-chd" or "extract-chd-dvd") && (reply.Status == MediaWorkerStatus.Encoded || reply.Encoding is not null) ||
            (operation is "inspect-rvz" or "inspect-iso") && reply.Status == MediaWorkerStatus.Ready ||
            operation == "inspect-rvz" && reply.IsoInspection is not null ||
            operation == "inspect-iso" && reply.Status == MediaWorkerStatus.Inspected && reply.IsoInspection is null)
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (reply.Inspection is { } facts && source is not null &&
            (facts.ArtifactBytes != source.FileSize || facts.ArtifactSha256 != source.FileSha256.ToString() || facts.LogicalBytes > _options.MaxLogicalBytes ||
                (operation == "inspect-chd" ? facts.EncodingProfile != "chd-import/v1" || facts.DiscKind is not null :
                 operation == "inspect-iso" ? facts.EncodingProfile != "identity/v1" || facts.LogicalBytes != facts.ArtifactBytes ||
                    facts.LogicalSha256 != facts.ArtifactSha256 : !RvzInspectionProfile.IsValid(facts.EncodingProfile))))
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (reply.CdInspection is { } cd && (source is null || cd.Header.ArtifactBytes != source.FileSize ||
            cd.ArtifactSha256 != source.FileSha256.ToString() || cd.Header.LogicalBytes > (ulong)_options.MaxLogicalBytes))
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        if (reply.Encoding is { } encoded && source is not null && destination is not null &&
            (encoded.SourceBytes != source.FileSize || encoded.SourceSha256 != source.FileSha256.ToString() ||
                encoded.LogicalBytes != logicalBytes || encoded.ArtifactBytes > destination.MaximumBytes ||
                encoded.EncoderBuild != rvzFamily?.EncoderBuild || encoded.RecipeVersion != RvzCanonicalRecipe.Id ||
                (OperatingSystem.IsWindows() ? encoded.PriorityKind != "windows-priority" || encoded.PriorityValue != 0x4000 :
                    encoded.PriorityKind != "unix-nice" || encoded.PriorityValue is < 10 or > 19)))
            return Finish(MediaWorkerResult.Failed(MediaWorkerFailure.Protocol));
        return Finish(reply.Status switch
        {
            MediaWorkerStatus.Inspected or MediaWorkerStatus.Encoded => new(reply, null),
            MediaWorkerStatus.Unsupported => chd || chdTransform ? new(reply, MediaWorkerFailure.Unsupported) : MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported),
            MediaWorkerStatus.Malformed => chd || chdTransform ? new(reply, MediaWorkerFailure.Malformed) : MediaWorkerResult.Failed(MediaWorkerFailure.Malformed),
            MediaWorkerStatus.LimitExceeded => chd || chdTransform ? new(reply, MediaWorkerFailure.LimitExceeded) : MediaWorkerResult.Failed(MediaWorkerFailure.LimitExceeded),
            MediaWorkerStatus.IoFailure => chd || chdTransform ? new(reply, MediaWorkerFailure.IoFailure) : MediaWorkerResult.Failed(MediaWorkerFailure.IoFailure),
            _ => MediaWorkerResult.Failed(MediaWorkerFailure.Protocol)
        });
    }

    internal ProcessStartInfo CreateStart(string operation, ITempFile? source, IWritableTempFile? destination = null, long logicalBytes = 0,
        RomIsoDiscKind? discKind = null, ChdmanCandidate? toolCandidate = null, CdJobContext? cdJob = null) =>
        CreateStartForPlatform(operation, source, destination, logicalBytes, OperatingSystem.IsLinux(), discKind, toolCandidate, cdJob);

    internal ProcessStartInfo CreateStartForPlatform(string operation, ITempFile? source, IWritableTempFile? destination,
        long logicalBytes, bool linux, RomIsoDiscKind? discKind = null, ChdmanCandidate? toolCandidate = null, CdJobContext? cdJob = null)
    {
        int cpuSeconds = operation is "encode-chd" or "encode-chd-cd" ? _options.ChdEncodeCpuTimeoutSeconds : _options.CpuTimeoutSeconds;
        bool reducedLinux = linux && (operation is "encode" or "encode-chd" or "extract-chd-dvd" or "encode-chd-cd");
        var start = new ProcessStartInfo(linux ? reducedLinux ? "/usr/bin/nice" : "/usr/bin/prlimit" : _options.DotnetPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_options.WorkerPath)!
        };
        if (reducedLinux)
        {
            // Linux nice belongs to a thread. Establish it before CLR creates its
            // worker threads; nice and prlimit exec without changing the child PID.
            foreach (string argument in new[] { "-n", "10", "/usr/bin/prlimit" })
                start.ArgumentList.Add(argument);
        }
        if (linux)
        {
            foreach (string argument in new[] { "--as=" + Number(_options.LinuxAddressSpaceBytes), "--data=" + Number(_options.LinuxDataBytes),
                "--cpu=" + Number(cpuSeconds), "--core=0", "--", _options.DotnetPath })
                start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add(_options.WorkerPath);
        start.ArgumentList.Add(operation);
        if (operation == "encode" && source is not null && destination is not null)
        {
            foreach (string argument in new[] { source.Path, destination.Path, Number(_options.MaxArtifactBytes),
                Number(_options.MaxLogicalBytes), Number(logicalBytes), Number(destination.MaximumBytes),
                RvzDiscFamily.For(discKind)?.Codec.WorkerArgument ?? "invalid" })
                start.ArgumentList.Add(argument);
        }
        else if (operation is "encode-chd" or "extract-chd-dvd" or "encode-chd-cd" && source is not null && destination is not null)
        {
            foreach (string argument in new[] { source.Path, destination.Path, Number(_options.MaxArtifactBytes),
                Number(_options.MaxLogicalBytes), Number(source.FileSize), Number(destination.MaximumBytes), source.FileSha256.ToString() })
                start.ArgumentList.Add(argument);
            if (operation == "extract-chd-dvd")
                start.ArgumentList.Add(Number(logicalBytes));
        }
        else if (source is not null)
            foreach (string argument in new[] { source.Path, Number(_options.MaxArtifactBytes), Number(_options.MaxLogicalBytes) })
                start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["DOTNET_GCHeapHardLimit"] = _options.MemoryBytes.ToString("x", CultureInfo.InvariantCulture);
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        if ((operation is "handshake-chd" or "inspect-chd" or "inspect-chd-cd" or "verify-chd-cd" or "reverify-chd-cd") && _chdCandidate is { } candidate)
        {
            start.Environment["ROMD_CHD_NATIVE_DIRECTORY"] = candidate.BuildDirectory;
            start.Environment["ROMD_CHD_NATIVE_IDENTITY"] = MediaWorkerProtocol.WriteNativeIdentity(candidate.ExpectedIdentity).GetRawText();
        }
        if (operation is "encode-chd" or "extract-chd-dvd" or "encode-chd-cd" && toolCandidate is { } tool)
        {
            start.Environment["ROMD_CHDMAN_DIRECTORY"] = tool.BuildDirectory;
            start.Environment["ROMD_CHDMAN_IDENTITY"] = MediaWorkerProtocol.WriteToolIdentity(tool.ExpectedIdentity).GetRawText();
        }
        if (cdJob is not null)
        {
            start.Environment["ROMD_CD_REQUEST_PATH"] = cdJob.Request.Path;
            start.Environment["ROMD_CD_REQUEST_SHA256"] = cdJob.Request.FileSha256.ToString();
            start.Environment["ROMD_CD_REQUEST_BYTES"] = Number(cdJob.Request.FileSize);
        }
        return start;
    }

    private static bool ValidChdEncodingRejectionPath(ChdDvdEncodingRejection rejection, ITempFile source,
        IWritableTempFile output, ChdmanCandidate candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Same(string expected) => string.Equals(rejection.Path, expected, comparison);
        if (rejection.Stage == ChdDvdEncodingStage.Source)
            return Same(source.Path);
        if (rejection.Stage == ChdDvdEncodingStage.Output)
            return Same(output.Path);
        if (rejection.Stage == ChdDvdEncodingStage.ToolVerification && rejection.CandidateRejection is { } nested)
        {
            if (!string.Equals(nested.Path, rejection.Path, comparison))
                return false;
            if (nested.Cause == "NativeArtifactIo")
                return Same(candidate.BinaryPath) || Same(candidate.ManifestPath);
            return Same(nested.Cause.StartsWith("Manifest", StringComparison.Ordinal) ? candidate.ManifestPath : candidate.BinaryPath);
        }
        return Same(candidate.BinaryPath);
    }

    private bool ValidChdRejectionPath(ChdRejection rejection, ITempFile? source)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Same(string expected) => string.Equals(rejection.Path, expected, comparison);
        if (rejection.Stage == ChdRejectionStage.NativeVerification && rejection.Cause == "NativeArtifactIo")
            return Same(_chdCandidate!.BinaryPath) || Same(_chdCandidate.ManifestPath);
        if (rejection.Stage is ChdRejectionStage.NativeVerification or ChdRejectionStage.NativeLoad)
            return Same(rejection.Cause.StartsWith("Manifest", StringComparison.Ordinal) ? _chdCandidate!.ManifestPath : _chdCandidate!.BinaryPath);
        return source is not null && Same(source.Path);
    }

    private bool ValidLinuxLimits(MediaWorkerLimits? limits, int cpuSeconds) => OperatingSystem.IsLinux()
        ? limits == new MediaWorkerLimits(_options.LinuxAddressSpaceBytes, _options.LinuxDataBytes, cpuSeconds, 0)
        : limits is null;
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal enum CpuObservation { Stopped, Deadline, Fault }
    internal async Task<CpuObservation> ObserveNativeCpuAsync(MediaChildProcess process, CancellationToken ct,
        Func<MediaChildProcess, CancellationToken, Task<TimeSpan>>? readCpu = null, int? cpuLimitSeconds = null)
    {
        while (!process.HasExited)
        {
            try
            {
                var cpu = readCpu is null ? process.Observer.TotalProcessorTime : await readCpu(process, ct);
                if (cpu.TotalSeconds >= (cpuLimitSeconds ?? _options.CpuTimeoutSeconds))
                    return CpuObservation.Deadline;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // CPU accounting can disappear before the sole native wait owner
                // publishes exit. Its successful wait is the stop proof.
                try
                {
                    await process.WaitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(250), ct);
                    return CpuObservation.Stopped;
                }
                catch (Exception waitError) when (waitError is TimeoutException or InvalidOperationException or Win32Exception)
                { return CpuObservation.Fault; }
            }
            await Task.Delay(50, ct);
        }
        return CpuObservation.Stopped;
    }
    private sealed record OutputCapture(byte[] Bytes, bool Overflow, long TotalBytes, bool ReadFailure);
    private static MediaWorkerDiagnosticKind DiagnosticKind(byte[] bytes) => bytes.Length == 0 ? MediaWorkerDiagnosticKind.None
        : bytes.AsSpan().IndexOf("Out of memory."u8) >= 0 ? MediaWorkerDiagnosticKind.RuntimeMemoryFailure
        : bytes.AsSpan().IndexOf("Failed to create CoreCLR"u8) >= 0 || bytes.AsSpan().IndexOf("You must install or update .NET"u8) >= 0
            ? MediaWorkerDiagnosticKind.RuntimeStartupFailure : MediaWorkerDiagnosticKind.ChildErrorOutput;
    private static async Task<OutputCapture> DrainAsync(Stream source, int maximum, TaskCompletionSource overflow, CancellationToken ct)
    {
        byte[] retained = new byte[maximum];
        byte[] buffer = new byte[4096];
        int length = 0;
        bool exceeded = false;
        long total = 0;
        bool readFailure = false;
        int read;
        try
        {
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                int keep = Math.Min(maximum - length, read);
                buffer.AsSpan(0, keep).CopyTo(retained.AsSpan(length));
                length += keep;
                if (keep < read)
                { exceeded = true; overflow.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { readFailure = !ct.IsCancellationRequested; }
        return new(retained[..length], exceeded, total, readFailure);
    }
}
