using System.IO.Abstractions;
using System.Text;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdCdWorkflowTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "romd-cd-workflow-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData("single")]
    [InlineData("multi")]
    [InlineData("partial")]
    public async Task VerifyChdCdAsync_FreshChild_MeasuresEveryWholeOriginalFile(string shape)
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);
        var supervisor = new MediaWorkerSupervisor(fixture.Options, candidate);
        var factory = Factory();
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        List<ITempFile> owned = [];
        try
        {
            async Task<ITempFile> Stage(string name)
            { await using var stream = File.OpenRead(NativeTestBuilds.LibchdrFixture(name)); var f = await factory.CreateAsync(stream); owned.Add(f); return f; }
            var cue = await Stage($"cd-edge-{shape}.cue");
            var plan = ChdCdCue.Parse(File.ReadAllBytes(cue.Path));
            Dictionary<string, ITempFile> bins = new();
            foreach (var file in plan.Files)
                bins.Add(file.Name, await Stage(file.Name));
            var sidecar = await factory.CreateAsync(new MemoryStream(new byte[] { 0x53, 0x42, 0x49, 0, 0xfe }));
            owned.Add(sidecar);
            await using var sources = await workflow.PrepareAsync("original.cue", cue, bins, new Dictionary<string, ITempFile> { ["original.sbi"] = sidecar });
            sources.Specification.ToolCuePath.ShouldEndWith(".cue");
            var artifact = await Stage($"cd-edge-{shape}.chd");
            var result = await supervisor.VerifyChdCdAsync(artifact, sources.Request, sources.Inputs);
            result.IsSuccess.ShouldBeTrue(result.Failure + ":" + result.Reply?.CdFailure);
            result.ProcessTreeStopped.ShouldBeTrue();
            result.Reply!.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
            result.Reply.CdVerification!.Members.Count.ShouldBe(bins.Count + 2);
            foreach (var member in result.Reply.CdVerification.Members.Where(m => m.Role == "bin"))
            {
                member.Original.ShouldBe(member.Reconstructed);
                member.Original.Sha256.ShouldBe(bins[member.Name].FileSha256.ToString());
                member.Original.Bytes.ShouldBe(bins[member.Name].FileSize);
            }
            var comparison = await workflow.VerifyExistingAsync(artifact, sources);
            var measured = comparison.Measured;
            var manifest = measured.Manifest.ShouldBeOfType<RomReconstructionManifest.CdTracks>();
            measured.Measurement.Outputs.Count.ShouldBe(bins.Count);
            manifest.Members.OfType<RomCdBinMember>().Count().ShouldBe(bins.Count);
            if (shape == "single")
                manifest.Members.OfType<RomCdBinMember>().Single().Tracks.ShouldBe([1, 2, 3]);
            manifest.Members.OfType<RomCdStoredMember>().Count().ShouldBe(2);
            MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
            foreach (var input in sources.Inputs.Append(sources.Request))
                File.Exists(input.Path + ".quarantine").ShouldBeFalse();
        }
        finally { foreach (var input in owned) await input.DisposeAsync(); }
    }

    [Fact]
    public async Task VerifyChdCdAsync_OriginalBinMismatch_CannotProduceArchiveEvidence()
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory));
        var factory = Factory();
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        await using var cue = await factory.CreateAsync(new MemoryStream(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.cue"))));
        byte[] bytes = File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.bin"));
        bytes[^1] ^= 1;
        await using var wrong = await factory.CreateAsync(new MemoryStream(bytes));
        await using var sources = await workflow.PrepareAsync("disc.cue", cue, new Dictionary<string, ITempFile> { ["cd-edge-single.bin"] = wrong });
        await using var chd = await factory.CreateAsync(new MemoryStream(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.chd"))));
        var result = await supervisor.VerifyChdCdAsync(chd, sources.Request, sources.Inputs);
        result.IsSuccess.ShouldBeFalse();
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Reply!.CdFailure!.Cause.ShouldBe(ChdCdJobCause.ReconstructionMismatch);
        result.Reply.CdVerification.ShouldBeNull();
        var failure = await Should.ThrowAsync<RomCdWorkerFailureException>(() => workflow.VerifyExistingAsync(chd, sources));
        failure.Failure.ShouldBe(MediaWorkerFailure.Malformed);
        failure.Message.ShouldContain(nameof(ChdCdJobCause.ReconstructionMismatch));
        failure.Message.ShouldNotContain(chd.Path);
        workflow.ProcessTreeStopped.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyChdCdAsync_ChangedTrackMode_RejectsEvenWhenRawBytesMatch()
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory));
        var factory = Factory();
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        string text = File.ReadAllText(NativeTestBuilds.LibchdrFixture("cd-edge-single.cue")).Replace("MODE2/2352", "MODE1/2352", StringComparison.Ordinal);
        await using var cue = await factory.CreateAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        await using var bin = await factory.CreateAsync(new MemoryStream(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.bin"))));
        await using var sources = await workflow.PrepareAsync("disc.cue", cue, new Dictionary<string, ITempFile> { ["cd-edge-single.bin"] = bin });
        await using var artifact = await factory.CreateAsync(new MemoryStream(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.chd"))));
        var result = await supervisor.VerifyChdCdAsync(artifact, sources.Request, sources.Inputs);
        result.IsSuccess.ShouldBeFalse();
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Reply!.CdFailure!.Cause.ShouldBe(ChdCdJobCause.ReconstructionMismatch);
        result.Reply.CdVerification.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyChdCdAsync_MissingCustodyOrChangedRequest_FailsBeforeLaunch()
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory));
        var factory = Factory();
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        await using var cue = await factory.CreateAsync(new MemoryStream("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00"u8.ToArray()));
        await using var bin = await factory.CreateAsync(new MemoryStream(new byte[2352 * 4]));
        await using var sources = await workflow.PrepareAsync("disc.cue", cue, new Dictionary<string, ITempFile> { ["disc.bin"] = bin });
        await using var artifact = await factory.CreateAsync(new MemoryStream(new byte[124]));
        await Should.ThrowAsync<InvalidDataException>(() => supervisor.VerifyChdCdAsync(artifact, sources.Request, sources.Inputs.Skip(1).ToArray()));
        File.WriteAllText(sources.Request.Path, "{}");
        await Should.ThrowAsync<EndOfStreamException>(() => supervisor.VerifyChdCdAsync(artifact, sources.Request, sources.Inputs));
    }

    [Fact]
    public async Task VerifyChdCdAsync_CancelledChild_KeepsEveryInputUntilTreeStops()
    {
        using var fixture = new MediaWorkerTestFixture(inspectionMode: "gated");
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(2L << 30);
        var runtime = new MediaProcessingRuntime(options, fixture.Options);
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new MediaWorkerSupervisor(fixture.Options, () => new MediaWorkerProcessLifetime(),
            setup: (process, point, _) => { if (point == MediaWorkerSetupPoint.Permitted) entered.TrySetResult(process.Id); return Task.CompletedTask; },
            runtime: runtime, chdCandidate: ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory));
        var factory = Factory();
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        await using var cue = await factory.CreateAsync(new MemoryStream("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00"u8.ToArray()));
        await using var bin = await factory.CreateAsync(new MemoryStream(new byte[2352 * 4]));
        await using var sources = await workflow.PrepareAsync("disc.cue", cue, new Dictionary<string, ITempFile> { ["disc.bin"] = bin });
        await using var artifact = await factory.CreateAsync(new MemoryStream(new byte[124]));
        using var cancel = new CancellationTokenSource();
        var running = supervisor.VerifyChdCdAsync(artifact, sources.Request, sources.Inputs, cancel.Token);
        int pid = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string journal = Path.Combine(_root, "temp", "media-runs-v1", "run-0.json");
        var record = MediaRunRecord.Read(journal, 0, fixture.Options.MaxConcurrent).ShouldNotBeNull();
        record.Staging.Select(binding => binding.Path).ShouldBe(sources.Inputs.Append(sources.Request).Append(artifact).Select(input => input.Path), ignoreOrder: true);
        foreach (var input in sources.Inputs.Append(sources.Request).Append(artifact))
        { File.Exists(input.Path).ShouldBeTrue(); File.Exists(input.Path + ".quarantine").ShouldBeTrue(); }
        cancel.Cancel();
        try
        { await running; Assert.Fail("Expected stopped cancellation."); }
        catch (MediaWorkerStoppedCancellationException error) { error.CancellationToken.ShouldBe(cancel.Token); }
        MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
        foreach (var input in sources.Inputs.Append(sources.Request).Append(artifact))
        { File.Exists(input.Path).ShouldBeTrue(); File.Exists(input.Path + ".quarantine").ShouldBeFalse(); }
        // Disposal is allowed after the stopped proof. Premature disposal intentionally quarantines permanently.
        await sources.DisposeAsync();
        await bin.DisposeAsync();
        await artifact.DisposeAsync();
        foreach (var path in new[] { sources.Request.Path, sources.Specification.ToolCuePath, bin.Path, artifact.Path })
            File.Exists(path).ShouldBeFalse();
        File.Exists(cue.Path).ShouldBeTrue(); // The caller still owns the un-disposed original descriptor.
    }

    [Fact]
    public async Task VerifyChdCdAsync_ChangedOriginalSource_RejectsWithoutComparisonProof()
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory));
        var factory = Factory();
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        await using var cue = await factory.CreateAsync(new MemoryStream(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.cue"))));
        await using var bin = await factory.CreateAsync(new MemoryStream(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.bin"))));
        await using var sources = await workflow.PrepareAsync("disc.cue", cue, new Dictionary<string, ITempFile> { ["cd-edge-single.bin"] = bin });
        await using var artifact = await factory.CreateAsync(new MemoryStream(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-edge-single.chd"))));
        using (var file = new FileStream(bin.Path, FileMode.Open, FileAccess.Write))
            file.WriteByte(0xff);
        var result = await supervisor.VerifyChdCdAsync(artifact, sources.Request, sources.Inputs);
        result.IsSuccess.ShouldBeFalse();
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Reply!.CdFailure!.Cause.ShouldBe(ChdCdJobCause.SourceMismatch);
        result.Reply.CdVerification.ShouldBeNull();
        result.Reply.CdInspection.ShouldBeNull();
    }

    private LocalTempFileFactory Factory()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(2L << 30);
        return new(options, new FileSystem(), new TempStorageOptions { MaximumFiles = 32, MaximumBytes = 4L << 30, MinimumFreeBytes = 0 });
    }
    public void Dispose() => Directory.Delete(_root, true);
}
