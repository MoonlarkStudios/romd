using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Infrastructure.Storage;
using Romd.Domain.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using System.IO.Abstractions;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.TestSupport;
using Romd.Media.Rvz;
using Shouldly;
using System.Text.Json;
using Romd.Media.Probe;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class MediaWorkerSupervisorTests(ITestOutputHelper testOutput)
{
    private static readonly SyntheticRvz FixtureDisc = new SyntheticRvzBuilder(0x100000, 0x20000).Build();
    private static readonly byte[] Disc = FixtureDisc.File;

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, true)]
    public async Task ExecuteAsync_PostStartSetupFault_JoinsRealGroupAndReusesSameAdmission(
        bool encode, int checkpoint, bool cancel)
    {
        var point = (MediaWorkerSetupPoint)checkpoint;
        using var fixture = new MediaWorkerTestFixture();
        byte[] original = encode ? SyntheticNintendoIso.GameCube() : Disc;
        await using var source = fixture.Source(original, "descendant-hang");
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int parent = 0;
        int descendant = 0;
        bool injected = false;
        var supervisor = new MediaWorkerSupervisor(fixture.Options, () => new MediaWorkerProcessLifetime(),
            setup: async (process, reached, ct) =>
            {
                if (injected || reached != point)
                    return;
                parent = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
                parent.ShouldBe(process.Id);
                descendant = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".descendant.pid", deadline.Token);
                MediaWorkerTestFixture.IsRunning(descendant).ShouldBeTrue();
                injected = true;
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    throw new OperationCanceledException(ct);
                }
                throw new IOException("injected post-start setup failure");
            });
        using var verifier = new RvzVerifier(fixture.Options, supervisor);
        string directory = Directory.CreateTempSubdirectory("romd-post-start-output-").FullName;
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(directory);
        options.MaxUploadBytes.Returns(8L << 20);
        var files = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions
        { MaximumBytes = 16L << 20, MaximumFiles = 2, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
        long cap = ((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(original.Length,
            fixture.Options.MaxLogicalBytes)).MaximumOutputBytes;
        await using var output = (await files.ReserveOutputAsync(cap)).Value;
        try
        {
            async Task ExecuteAsync(CancellationToken ct)
            {
                if (encode)
                {
                    using var admission = await verifier.AcquireAsync(ct);
                    var result = await admission.EncodeAsync(source, output, original.Length, RomIsoDiscKind.GameCube, ct);
                    result.IsError.ShouldBeTrue();
                    result.FirstError.Code.ShouldBe("Rom.Media.IoFailure");
                }
                else
                {
                    var result = await verifier.VerifyAsync(source, ct);
                    result.IsError.ShouldBeTrue();
                    result.FirstError.Code.ShouldBe("Rom.Media.IoFailure");
                }
            }
            if (cancel)
            {
                var error = await Should.ThrowAsync<OperationCanceledException>(() => ExecuteAsync(cancellation.Token));
                error.CancellationToken.ShouldBe(cancellation.Token);
            }
            else
                await ExecuteAsync(cancellation.Token);
            injected.ShouldBeTrue();
            MediaWorkerTestFixture.IsRunning(parent).ShouldBeFalse();
            MediaWorkerTestFixture.IsRunning(descendant).ShouldBeFalse();
            File.Exists(source.Path + ".quarantine").ShouldBeFalse();
            File.Exists(output.Path + ".quarantine").ShouldBeFalse();
            await using var sibling = fixture.Source(Disc);
            (await verifier.VerifyAsync(sibling, deadline.Token)).IsError.ShouldBeFalse();
            await output.DisposeAsync();
            await using var recovered = (await files.ReserveOutputAsync(8L << 20)).Value;
        }
        finally
        {
            // The red test must not leave its deliberately started group behind.
            if (parent != 0 && MediaWorkerTestFixture.IsRunning(parent))
            {
                using var process = Process.GetProcessById(parent);
                using var lifetime = new MediaWorkerProcessLifetime();
                lifetime.Terminate(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await lifetime.JoinDescendantsAsync(parent, CancellationToken.None);
                source.TryEndChildUse();
                output.TryEndChildUse();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectIsoAsync_SupportedOrTinyUnknownContent_ReturnsOperationSpecificMeasurement(bool supported)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(supported ? SyntheticNintendoIso.GameCube() : [1, 2, 3]);
        var supervisor = fixture.Supervisor();

        var result = await supervisor.InspectIsoAsync(source);

        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        result.Reply!.Inspection!.ArtifactSha256.ShouldBe(source.FileSha256.ToString());
        result.Reply.Inspection.LogicalSha256.ShouldBe(source.FileSha256.ToString());
        result.Reply.Inspection.LogicalBytes.ShouldBe(source.FileSize);
        result.Reply.Inspection.EncodingProfile.ShouldBe("identity/v1");
        result.Reply.IsoInspection!.Status.ShouldBe(supported ? NintendoIsoInspectionStatus.Eligible : NintendoIsoInspectionStatus.Unsupported);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
    }

    [Theory]
    [InlineData("iso-wrong-artifact")]
    [InlineData("iso-wrong-hash")]
    [InlineData("iso-wrong-length")]
    [InlineData("iso-invalid-counts")]
    [InlineData("iso-missing-facts")]
    [InlineData("malformed-output")]
    public async Task InspectIsoAsync_InvalidReply_FailsOnlyFileAndAllowsValidSibling(string mode)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var poison = fixture.Source(SyntheticNintendoIso.GameCube(), mode);
        await using var sibling = fixture.Source(SyntheticNintendoIso.Wii(1));
        var supervisor = fixture.Supervisor();
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();

        var result = await supervisor.InspectIsoAsync(poison);

        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        supervisor.IsAvailable.ShouldBeTrue();
        (await supervisor.InspectIsoAsync(sibling)).IsSuccess.ShouldBeTrue();
        File.Exists(poison.Path + ".quarantine").ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectIsoAsync_CrossOperationReply_IsRejected(bool requestingIso)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(requestingIso ? Disc : SyntheticNintendoIso.GameCube(),
            requestingIso ? "rvz-reply-for-iso" : "iso-reply-for-rvz");
        var supervisor = fixture.Supervisor();

        var result = requestingIso ? await supervisor.InspectIsoAsync(source) : await supervisor.InspectAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObserveNativeCpuAsync_ReadFailsAfterOrBeforeLeaderExit_DistinguishesStopFromMonitoringFailure(bool exits, bool nativeError)
    {
        using var fixture = new MediaWorkerTestFixture(inspectionMode: "gated");
        await using var source = fixture.Source(Disc, "gated");
        var supervisor = fixture.Supervisor();
        using var run = await supervisor.Runtime.AcquireAsync();
        using var lifetime = new MediaWorkerProcessLifetime();
        var start = supervisor.CreateStart("inspect-rvz", source);
        if (lifetime.JobName is { } job)
        {
            start.ArgumentList.Add("--job");
            start.ArgumentList.Add(job);
        }
        using (var binary = File.OpenRead(fixture.Options.WorkerPath))
        using (var pe = new System.Reflection.PortableExecutable.PEReader(binary))
        {
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            run.Prepare("inspect-rvz", "romd-media-worker/mvid:" + metadata.GetGuid(metadata.GetModuleDefinition().Mvid),
                lifetime.JobName, source, null);
        }
        source.TryBeginChildUse().ShouldBeTrue();
        foreach (string argument in new[] { "--run", run.RecordPath, "--nonce", run.Nonce })
            start.ArgumentList.Add(argument);
        run.MarkLaunching();
        using var process = MediaChildProcess.Start(start, run.NativeLease);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await run.PermitAsync(process.Ready, process.Input, process.Id, deadline.Token);
            await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
            int samples = 0;
            var result = await supervisor.ObserveNativeCpuAsync(process, deadline.Token, async (child, ct) =>
            {
                samples++;
                child.HasExited.ShouldBeFalse("the leader is alive before its CPU read");
                if (exits)
                {
                    fixture.Release();
                    await child.WaitAsync(ct);
                }
                throw nativeError ? new Win32Exception(3) : new InvalidOperationException("CPU read unavailable");
            });
            samples.ShouldBe(1);
            result.ShouldBe(exits ? MediaWorkerSupervisor.CpuObservation.Stopped : MediaWorkerSupervisor.CpuObservation.Fault);
        }
        finally
        {
            process.Terminate(lifetime);
            await process.WaitAsync(CancellationToken.None);
            await lifetime.JoinDescendantsAsync(process.Id, CancellationToken.None);
            run.ConfirmStopped();
            source.TryEndChildUse().ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ObserveNativeCpuAsync_LeaderStoppedBeforeWaitCompletion_AwaitsOwnedReaper(bool nativeError, bool cancelWait)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new MediaWorkerTestFixture(inspectionMode: "gated");
        await using var source = fixture.Source(Disc, "gated");
        var supervisor = fixture.Supervisor();
        using var run = await supervisor.Runtime.AcquireAsync();
        using var lifetime = new MediaWorkerProcessLifetime();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = supervisor.CreateStart("inspect-rvz", source);
        using (var binary = File.OpenRead(fixture.Options.WorkerPath))
        using (var pe = new System.Reflection.PortableExecutable.PEReader(binary))
        {
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            run.Prepare("inspect-rvz", "romd-media-worker/mvid:" + metadata.GetGuid(metadata.GetModuleDefinition().Mvid),
                lifetime.JobName, source, null);
        }
        source.TryBeginChildUse().ShouldBeTrue();
        foreach (string argument in new[] { "--run", run.RecordPath, "--nonce", run.Nonce })
            start.ArgumentList.Add(argument);
        run.MarkLaunching();
        var reaperContext = new HeldReaperContext();
        var originalContext = SynchronizationContext.Current;
        MediaChildProcess process;
        try
        {
            SynchronizationContext.SetSynchronizationContext(reaperContext);
            process = MediaChildProcess.Start(start, run.NativeLease);
        }
        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }
        using var ownedProcess = process;
        try
        {
            await run.PermitAsync(process.Ready, process.Input, process.Id, deadline.Token);
            await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
            fixture.Release();
            await Task.WhenAll(process.Output.CopyToAsync(Stream.Null, deadline.Token),
                process.Error.CopyToAsync(Stream.Null, deadline.Token));
            process.HasExited.ShouldBeFalse("only the native wait-owner continuation is held after the leader closes stdout");
            int samples = 0;
            var observation = supervisor.ObserveNativeCpuAsync(process, deadline.Token, (_, _) =>
            {
                samples++;
                return Task.FromException<TimeSpan>(nativeError
                    ? new Win32Exception(3) : new InvalidOperationException("CPU accounting unavailable after exit"));
            });
            observation.IsCompleted.ShouldBeFalse("CPU accounting failure waits for its existing native exit owner");
            if (cancelWait)
            {
                await deadline.CancelAsync();
                var canceled = await Should.ThrowAsync<OperationCanceledException>(() => observation);
                canceled.CancellationToken.ShouldBe(deadline.Token);
                samples.ShouldBe(1);
                return;
            }
            reaperContext.Release();
            var result = await observation;
            result.ShouldBe(MediaWorkerSupervisor.CpuObservation.Stopped);
            samples.ShouldBe(1);
            await process.WaitAsync(deadline.Token);
        }
        finally
        {
            fixture.Release();
            reaperContext.Release();
            try
            { await process.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                process.Terminate(lifetime);
                await process.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                await lifetime.JoinDescendantsAsync(process.Id, CancellationToken.None);
                run.ConfirmStopped();
                source.TryEndChildUse().ShouldBeTrue();
            }
        }
    }

    private sealed class HeldReaperContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly List<(SendOrPostCallback Callback, object? State)> _pending = [];
        private bool _released;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                if (!_released)
                { _pending.Add((callback, state)); return; }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        internal void Release()
        {
            (SendOrPostCallback Callback, object? State)[] pending;
            lock (_gate)
            {
                _released = true;
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var item in pending)
                ThreadPool.QueueUserWorkItem(_ => item.Callback(item.State));
        }
    }

    [Fact]
    public async Task HandshakeAsync_FreshChildren_ProvesPackagedIdentitySessionAndHeapConfiguration()
    {
        var options = new MediaProcessingOptions();
        var supervisor = new MediaWorkerSupervisor(options);
        var first = await supervisor.HandshakeAsync();
        first.IsSuccess.ShouldBeTrue(first.Failure?.ToString());
        var second = await supervisor.HandshakeAsync();
        second.IsSuccess.ShouldBeTrue(second.Failure?.ToString());
        first.Reply!.Identity.ProcessId.ShouldNotBe(second.Reply!.Identity.ProcessId);
        first.Reply.Identity.GcHeapLimitBytes.ShouldBe(options.MemoryBytes);
        first.Reply.Identity.DecoderBuild.ShouldBe(MediaInspectionService.DecoderBuild);
        first.Reply.Identity.WorkerBuild.ShouldStartWith("romd-media-worker/mvid:");
        if (!OperatingSystem.IsWindows())
            first.Reply.Identity.SessionId.ShouldBe(first.Reply.Identity.ProcessId);
        supervisor.IsAvailable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("bad-heap")]
    [InlineData("bad-build")]
    [InlineData("malformed-output")]
    [InlineData("empty")]
    public async Task HandshakeAsync_InvalidChild_FailsClosed(string mode)
    {
        using var fixture = new MediaWorkerTestFixture(startupMode: mode);
        var supervisor = fixture.Supervisor();
        var result = await new MediaWorkerStartupValidator(supervisor).ValidateAsync();
        result.Failure.ShouldBe(MediaWorkerFailure.Startup);
        supervisor.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task HandshakeAsync_MissingOrInvalidBinary_FailsBeforeFileWork()
    {
        using var fixture = new MediaWorkerTestFixture();
        File.Delete(fixture.Options.WorkerPath);
        (await fixture.Supervisor().HandshakeAsync()).Failure.ShouldBe(MediaWorkerFailure.Startup);
        await File.WriteAllBytesAsync(fixture.Options.WorkerPath, [1, 2, 3, 4]);
        (await fixture.Supervisor().HandshakeAsync()).Failure.ShouldBe(MediaWorkerFailure.Startup);
    }

    [Theory]
    [InlineData("handshake")]
    [InlineData("encode")]
    public async Task Worker_HandshakeOrEncodeWithInspectionArguments_RejectsBeforeFileWork(string operation)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(Disc);
        using var lifetime = new MediaWorkerProcessLifetime();
        var start = fixture.Supervisor().CreateStart(operation, source);
        if (lifetime.JobName is { } job)
        {
            start.ArgumentList.Add("--job");
            start.ArgumentList.Add(job);
        }
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        process.Start().ShouldBeTrue();
        process.StandardInput.Close();
        try
        { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            lifetime.Terminate(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await lifetime.JoinDescendantsAsync(process.Id, CancellationToken.None);
        }
        process.ExitCode.ShouldBe(64);
        process.StandardOutput.BaseStream.ReadByte().ShouldBe(-1);
        process.StandardError.BaseStream.ReadByte().ShouldBe(-1);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("Wii")]
    [InlineData(" wii")]
    [InlineData("2")]
    public async Task Worker_EncodeRequiresExactDiscKind_RejectsOldArityOrInvalidKindWithoutOutput(string kind)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticNintendoIso.GameCube());
        string temporary = Directory.CreateTempSubdirectory("romd-encode-cli-").FullName;
        try
        {
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(temporary);
            options.MaxUploadBytes.Returns(8L << 20);
            var files = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions
            { MaximumBytes = 16L << 20, MaximumFiles = 2, MinimumFreeBytes = 0 });
            await using var output = (await files.ReserveOutputAsync(1L << 20)).Value;
            using var lifetime = new MediaWorkerProcessLifetime();
            var start = fixture.Supervisor().CreateStart("encode", source, output, source.FileSize, RomIsoDiscKind.GameCube);
            if (kind == "absent")
                start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
            else
                start.ArgumentList[^1] = kind;
            if (lifetime.JobName is { } job)
            { start.ArgumentList.Add("--job"); start.ArgumentList.Add(job); }
            using var process = new Process { StartInfo = start };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            process.Start().ShouldBeTrue();
            process.StandardInput.Close();
            try
            { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                lifetime.Terminate(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await lifetime.JoinDescendantsAsync(process.Id, CancellationToken.None);
            }
            process.ExitCode.ShouldBe(64);
            process.StandardOutput.BaseStream.ReadByte().ShouldBe(-1);
            process.StandardError.BaseStream.ReadByte().ShouldBe(-1);
            File.Exists(output.Path).ShouldBeFalse();
        }
        finally { Directory.Delete(temporary, true); }
    }

    [Theory]
    [InlineData("bad-heap")]
    [InlineData("bad-build")]
    public async Task InspectAsync_InvalidAdmittedIdentity_IsProtocolFailureWithoutDisablingSibling(string mode)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var poison = fixture.Source(Disc, mode);
        await using var sibling = fixture.Source(Disc);
        var supervisor = fixture.Supervisor();
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        var failed = await supervisor.InspectAsync(poison);
        failed.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        supervisor.IsAvailable.ShouldBeTrue();
        (await supervisor.InspectAsync(sibling)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("empty", MediaWorkerFailure.Protocol)]
    [InlineData("malformed-output", MediaWorkerFailure.Protocol)]
    [InlineData("truncated-output", MediaWorkerFailure.Protocol)]
    [InlineData("stdout-overflow", MediaWorkerFailure.OutputLimit)]
    [InlineData("diagnostic-overflow", MediaWorkerFailure.OutputLimit)]
    [InlineData("crash", MediaWorkerFailure.Crash)]
    [InlineData("success-nonzero", MediaWorkerFailure.Crash)]
    [InlineData("wrong-artifact", MediaWorkerFailure.Protocol)]
    [InlineData("wrong-profile", MediaWorkerFailure.Protocol)]
    [InlineData("oversized-logical", MediaWorkerFailure.Protocol)]
    public async Task InspectAsync_PoisonChild_FailsOnlyThatFileAndReapsBeforeValidSibling(string mode, MediaWorkerFailure expected)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var poison = fixture.Source(Disc, mode);
        await using var sibling = fixture.Source(Disc);
        var supervisor = fixture.Supervisor();
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        var failed = await supervisor.InspectAsync(poison);
        failed.Failure.ShouldBe(expected);
        var execution = failed.Execution.ShouldNotBeNull();
        if (mode == "stdout-overflow")
            execution.StandardOutputBytes.ShouldBe(8193);
        if (mode == "diagnostic-overflow")
            execution.StandardErrorBytes.ShouldBe(16385);
        MediaWorkerTestFixture.IsRunning(int.Parse(await File.ReadAllTextAsync(poison.Path + ".pid"))).ShouldBeFalse();
        supervisor.IsAvailable.ShouldBeTrue();
        (await supervisor.InspectAsync(sibling)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InspectAsync_ClearedEnvironment_OnlyExplicitRuntimeKnobsReachChild()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(Disc, "environment");
        var start = fixture.Supervisor().CreateStart("inspect-rvz", source);
        start.Environment.Keys.Order(StringComparer.Ordinal).ShouldBe(new[]
        {
            "DOTNET_EnableDiagnostics", "DOTNET_GCHeapHardLimit", "DOTNET_PROCESSOR_COUNT", "DOTNET_gcServer"
        }.Order(StringComparer.Ordinal));
        (await fixture.Supervisor().InspectAsync(source)).IsSuccess.ShouldBeTrue(await File.ReadAllTextAsync(source.Path + ".environment-names"));
    }

    [Theory]
    [InlineData("private-diagnostic", MediaWorkerDiagnosticKind.ChildErrorOutput, 23)]
    [InlineData("runtime-oom-diagnostic", MediaWorkerDiagnosticKind.RuntimeMemoryFailure, 14)]
    public async Task InspectAsync_ChildDiagnostics_RetainsOnlySafeExecutionFacts(string mode, MediaWorkerDiagnosticKind kind, long bytes)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(Disc, mode);
        var logger = Substitute.For<ILogger<MediaWorkerSupervisor>>();
        var failed = await new MediaWorkerSupervisor(fixture.Options, logger).InspectAsync(source);
        failed.Failure.ShouldBe(MediaWorkerFailure.Crash);
        var execution = failed.Execution.ShouldNotBeNull();
        execution.ExitCode.ShouldBe(17);
        execution.StandardOutputBytes.ShouldBe(0);
        execution.StandardErrorBytes.ShouldBe(bytes);
        execution.DiagnosticKind.ShouldBe(kind);
        failed.ToString().ShouldNotContain("fixture-private-content");
        var logs = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log").ToArray();
        logs.ShouldHaveSingleItem();
        logs[0].GetArguments()[2]!.ToString().ShouldNotBeNull().ShouldNotContain("fixture-private-content");
        MediaWorkerTestFixture.IsRunning(execution.ProcessId).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_ManagedHeapLimit_RejectsOversizedAllocationAndRecovers()
    {
        using var fixture = new MediaWorkerTestFixture();
        fixture.Options.MemoryBytes = 64L << 20;
        await using var source = fixture.Source(Disc, "oom");
        await using var sibling = fixture.Source(Disc);
        var supervisor = fixture.Supervisor();
        (await supervisor.InspectAsync(source)).Failure.ShouldBe(MediaWorkerFailure.LimitExceeded);
        (await supervisor.InspectAsync(sibling)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InspectAsync_NativeDataLimit_ExportsActualFixtureAndRequiresLinuxEnforcement()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(Disc, "native-memory");
        await using var control = fixture.Source(Disc, "native-control");
        await using var sibling = fixture.Source(Disc);
        await using var descendant = fixture.Source(Disc, "descendant-exit");
        await using var blocked = fixture.Source(Disc, "descendant-hang");
        await using var managed = fixture.Source(Disc, "oom");
        await using var managedReject = fixture.Source(Disc, "managed-reject");
        await using var managedControl = fixture.Source(Disc, "managed-control");
        await using var crash = fixture.Source(Disc, "crash");
        await using var cpu = fixture.Source(Disc, "cpu");
        if (Environment.GetEnvironmentVariable("ROMD_MEDIA_FAULT_EXPORT") is { } output)
            fixture.ExportTo(output, FixtureDisc.ExpectedDisc);
        var supervisor = fixture.Supervisor();
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        var result = await supervisor.InspectAsync(source);
        if (OperatingSystem.IsLinux())
        {
            result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
            result.Reply!.Identity.LinuxLimits.ShouldBe(new MediaWorkerLimits(fixture.Options.LinuxAddressSpaceBytes,
                fixture.Options.LinuxDataBytes, fixture.Options.CpuTimeoutSeconds, 0));
            (await File.ReadAllTextAsync(source.Path + ".native-probe")).ShouldBe($"smallTouched=65536;requested=1140850688;largeRejected=True;data={fixture.Options.LinuxDataBytes}");
        }
        else
            result.Failure.ShouldBe(MediaWorkerFailure.Unsupported);
        (await supervisor.InspectAsync(sibling)).IsSuccess.ShouldBeTrue();
        fixture.Options.LinuxDataBytes = 2L << 30;
        var withLargerData = await fixture.Supervisor().InspectAsync(control);
        if (OperatingSystem.IsLinux())
        {
            withLargerData.IsSuccess.ShouldBeTrue(withLargerData.Failure?.ToString());
            withLargerData.Reply!.Identity.GcHeapLimitBytes.ShouldBe(512L << 20);
            withLargerData.Reply.Identity.LinuxLimits!.DataBytes.ShouldBe(2L << 30);
            (await File.ReadAllTextAsync(control.Path + ".native-probe")).ShouldBe("smallTouched=65536;requested=1140850688;largeRejected=False;data=2147483648");
        }
        else
            withLargerData.Failure.ShouldBe(MediaWorkerFailure.Unsupported);
    }

    [Fact]
    public async Task InspectAsync_FixedManagedRequest_DistinguishesGcLimitFromUnchangedOsBudgets()
    {
        using var fixture = new MediaWorkerTestFixture();
        fixture.Options.LinuxDataBytes = 2L << 30;
        await using var rejected = fixture.Source(Disc, "managed-reject");
        var rejection = await fixture.Supervisor().InspectAsync(rejected);
        rejection.Failure.ShouldBe(MediaWorkerFailure.LimitExceeded);
        (await File.ReadAllTextAsync(rejected.Path + ".managed-probe"))
            .ShouldBe("requested=536870913;largeRejected=True;gc=536870912");
        fixture.Options.MemoryBytes = 1L << 30;
        await using var accepted = fixture.Source(Disc, "managed-control");
        var control = await fixture.Supervisor().InspectAsync(accepted);
        control.IsSuccess.ShouldBeTrue(control.Failure?.ToString());
        control.Reply!.Inspection!.LogicalBytes.ShouldBe(FixtureDisc.ExpectedDisc.Length);
        (await File.ReadAllTextAsync(accepted.Path + ".managed-probe"))
            .ShouldBe("requested=536870913;largeRejected=False;gc=1073741824");
        if (OperatingSystem.IsLinux())
            control.Reply.Identity.LinuxLimits!.DataBytes.ShouldBe(2L << 30);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_DeadlineOrCancellation_JoinsParentAndDescendantsBeforeReturning(bool cancel)
    {
        using var fixture = new MediaWorkerTestFixture();
        fixture.Options.WallTimeoutSeconds = cancel ? 10 : 1;
        await using var source = fixture.Source(Disc, "descendant-hang");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Supervisor().InspectAsync(source, cancellation.Token);
        int parent = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
        int descendant = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".descendant.pid", deadline.Token);
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        }
        else
            (await pending).Failure.ShouldBe(MediaWorkerFailure.WallDeadline);
        MediaWorkerTestFixture.IsRunning(parent).ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(descendant).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_ExitedLeader_StillKillsAndJoinsDescendant()
    {
        using var fixture = new MediaWorkerTestFixture();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await using var source = fixture.Source(Disc, "descendant-exit");
            var result = await fixture.Supervisor().InspectAsync(source);
            result.Failure.ShouldBe(MediaWorkerFailure.Crash);
            int descendant = int.Parse(await File.ReadAllTextAsync(source.Path + ".descendant.pid"));
            MediaWorkerTestFixture.IsRunning(descendant).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task InspectAsync_CpuDeadline_IsIndependentOfWallDeadline()
    {
        using var fixture = new MediaWorkerTestFixture();
        fixture.Options.CpuTimeoutSeconds = 1;
        await using var source = fixture.Source(Disc, "cpu");
        long began = Stopwatch.GetTimestamp();
        var supervisor = fixture.Supervisor();
        var result = await supervisor.InspectAsync(source);
        double elapsed = Stopwatch.GetElapsedTime(began).TotalSeconds;
        testOutput.WriteLine(JsonSerializer.Serialize(new
        {
            Mode = "cpu",
            fixture.Options.CpuTimeoutSeconds,
            fixture.Options.WallTimeoutSeconds,
            Seconds = elapsed,
            Result = result,
            Scope = "Combined supervisor/kernel CPU enforcement; no kernel-only attribution."
        }));
        result.Failure.ShouldBeOneOf(MediaWorkerFailure.CpuDeadline, MediaWorkerFailure.Crash);
        elapsed.ShouldBeLessThan(5);
        SupervisorChecks.AssertCpuDeadline(result, elapsed, fixture.Options.CpuTimeoutSeconds, fixture.Options.WallTimeoutSeconds);
        File.Exists(source.Path + ".pid").ShouldBeTrue("the CPU payload must have started after permission");
        int.Parse(await File.ReadAllTextAsync(source.Path + ".pid", CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture).ShouldBe(result.Execution!.ProcessId);
        await using var sibling = fixture.Source(Disc);
        var recovered = await supervisor.InspectAsync(sibling);
        recovered.IsSuccess.ShouldBeTrue("the same supervisor must recover after the CPU burn");
        recovered.Execution!.ProcessId.ShouldNotBe(result.Execution.ProcessId);
        DefaultProcessingChecks.AssertStopped(recovered);
    }

}
