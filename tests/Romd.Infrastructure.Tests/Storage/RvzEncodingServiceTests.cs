using System.IO.Abstractions;
using System.Security.Cryptography;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RvzEncodingServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-encode-staging-").FullName;
    private LocalTempFileFactory Files(long total = 16L << 20, int slots = 4)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(8L << 20);
        return new(options, new FileSystem(), new TempStorageOptions
        { MaximumBytes = total, MaximumFiles = slots, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStartForPlatform_Linux_UsesPreClrNiceOnlyForEncodingAndKeepsExactLimitsEnvironment(bool encoding)
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original);
        long cap = ((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, child.Options.MaxLogicalBytes)).MaximumOutputBytes;
        await using var output = (await Files().ReserveOutputAsync(cap)).Value;
        var start = child.Supervisor().CreateStartForPlatform(encoding ? "encode" : "inspect-rvz", source,
            encoding ? output : null, encoding ? original.Length : 0, linux: true, discKind: encoding ? RomIsoDiscKind.GameCube : null);
        var arguments = new List<string>();
        if (encoding)
            arguments.AddRange(["-n", "10", "/usr/bin/prlimit"]);
        static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        arguments.AddRange(["--as=" + Number(child.Options.LinuxAddressSpaceBytes), "--data=" + Number(child.Options.LinuxDataBytes),
            "--cpu=" + Number(child.Options.CpuTimeoutSeconds), "--core=0", "--", child.Options.DotnetPath,
            child.Options.WorkerPath, encoding ? "encode" : "inspect-rvz", source.Path]);
        if (encoding)
            arguments.Add(output.Path);
        arguments.AddRange([Number(child.Options.MaxArtifactBytes), Number(child.Options.MaxLogicalBytes)]);
        if (encoding)
            arguments.AddRange([Number(original.Length), Number(cap), "gamecube"]);
        start.FileName.ShouldBe(encoding ? "/usr/bin/nice" : "/usr/bin/prlimit");
        start.ArgumentList.ToArray().ShouldBe(arguments.ToArray());
        start.UseShellExecute.ShouldBeFalse();
        start.RedirectStandardOutput.ShouldBeTrue();
        start.RedirectStandardError.ShouldBeTrue();
        start.RedirectStandardInput.ShouldBeTrue();
        start.Environment.Keys.Order(StringComparer.Ordinal).ShouldBe(new[]
        { "DOTNET_EnableDiagnostics", "DOTNET_GCHeapHardLimit", "DOTNET_PROCESSOR_COUNT", "DOTNET_gcServer" });
        start.Environment["DOTNET_GCHeapHardLimit"].ShouldBe(child.Options.MemoryBytes.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        start.Environment["DOTNET_gcServer"].ShouldBe("0");
        start.Environment["DOTNET_PROCESSOR_COUNT"].ShouldBe("2");
        start.Environment["DOTNET_EnableDiagnostics"].ShouldBe("0");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(19)]
    public async Task EncodeAsync_AlreadyReducedParentPriority_PreservesUnixNiceOrAppliesWindowsClass(int inheritedNice)
    {
        var options = new MediaProcessingOptions { MaxConcurrent = 1 };
        string launchedPid = Path.Combine(_root, "launched.pid");
        if (!OperatingSystem.IsWindows())
        {
            File.Exists("/usr/bin/nice").ShouldBeTrue("this actual Unix launcher requires the installed OS utility");
            string wrapper = Path.Combine(_root, "nice-dotnet");
            static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
            await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nprintf '%s' \"$$\" > " + Quote(launchedPid) +
                "\nexec /usr/bin/nice -n " + inheritedNice.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " " + Quote(options.DotnetPath) + " \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.DotnetPath = wrapper;
        }
        var capture = new CapturingSupervisor(new MediaWorkerSupervisor(options));
        using var verifier = new RvzVerifier(options, capture);
        var files = Files();
        await using var source = await files.CreateAsync(new MemoryStream(SyntheticNintendoIso.GameCube()));
        var result = await new RvzEncodingService(verifier, files, options).EncodeAsync(source, source.FileSize, RomIsoDiscKind.GameCube);
        result.IsError.ShouldBeFalse("an already reduced child must encode; failure=" + capture.EncodingResult?.Failure);
        await using var artifact = result.Value.Artifact;
        var child = capture.EncodingResult.ShouldNotBeNull();
        var facts = child.Reply!.Encoding.ShouldNotBeNull();
        child.Reply.Inspection.ShouldBeNull();
        child.Reply.IsoInspection.ShouldBeNull();
        facts.SourceSha256.ShouldBe(source.FileSha256.ToString());
        facts.SourceBytes.ShouldBe(source.FileSize);
        using var binary = File.OpenRead(options.WorkerPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(binary);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        child.Reply.Identity.WorkerBuild.ShouldBe("romd-media-worker/mvid:" + metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
        child.Reply.Identity.DecoderBuild.ShouldBe(MediaInspectionService.DecoderBuild);
        if (OperatingSystem.IsWindows())
        {
            facts.PriorityKind.ShouldBe("windows-priority");
            facts.PriorityValue.ShouldBe(0x4000);
        }
        else
        {
            facts.PriorityKind.ShouldBe("unix-nice");
            facts.PriorityValue.ShouldBeInRange(inheritedNice, 19);
            child.Execution!.ProcessId.ShouldBe(int.Parse(await File.ReadAllTextAsync(launchedPid)), "exec must preserve the launched child PID");
            child.Reply.Identity.SessionId.ShouldBe(child.Execution.ProcessId);
        }
        child.ProcessTreeStopped.ShouldBeTrue();
        MediaWorkerTestFixture.IsRunning(child.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        File.Exists(artifact.Path + ".quarantine").ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_SupervisedChild_ReturnsAppliedPriorityAndSourceBindingWithoutVerificationFacts()
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original);
        long cap = ((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, child.Options.MaxLogicalBytes)).MaximumOutputBytes;
        await using var output = (await Files().ReserveOutputAsync(cap)).Value;
        var result = await child.Supervisor().EncodeAsync(source, output, original.Length, RomIsoDiscKind.GameCube);
        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        var facts = result.Reply!.Encoding.ShouldNotBeNull();
        facts.SourceSha256.ShouldBe(source.FileSha256.ToString());
        facts.SourceBytes.ShouldBe(source.FileSize);
        facts.LogicalBytes.ShouldBe(original.Length);
        facts.PriorityKind.ShouldBe(OperatingSystem.IsWindows() ? "windows-priority" : "unix-nice");
        if (OperatingSystem.IsWindows())
            facts.PriorityValue.ShouldBe(0x4000);
        else
            facts.PriorityValue.ShouldBeInRange(10, 19);
        result.Reply.Inspection.ShouldBeNull();
        result.Reply.IsoInspection.ShouldBeNull();
        result.Execution!.StandardOutputBytes.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        result.ProcessTreeStopped.ShouldBeTrue();
        MediaWorkerTestFixture.IsRunning(result.Execution.ProcessId).ShouldBeFalse();
        File.Exists(output.Path + ".quarantine").ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_OutputCapBelowCanonicalBound_ReturnsPermanentLimitAndEndsBothCustodies()
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original);
        await using var output = (await Files().ReserveOutputAsync(0x124)).Value;
        var result = await child.Supervisor().EncodeAsync(source, output, original.Length, RomIsoDiscKind.GameCube);
        result.Failure.ShouldBe(MediaWorkerFailure.LimitExceeded);
        result.ProcessTreeStopped.ShouldBeTrue();
        File.Exists(output.Path).ShouldBeFalse();
        File.Exists(output.Path + ".quarantine").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_OutputHoldCannotPersist_DoesNotLaunchAndReleasesSourceHold()
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original);
        long cap = ((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, child.Options.MaxLogicalBytes)).MaximumOutputBytes;
        await using var output = (await Files().ReserveOutputAsync(cap)).Value;
        Directory.CreateDirectory(output.Path + ".quarantine");
        try
        {
            var result = await child.Supervisor().EncodeAsync(source, output, original.Length, RomIsoDiscKind.GameCube);
            result.Failure.ShouldBe(MediaWorkerFailure.SourceCustody);
            File.Exists(source.Path + ".pid").ShouldBeFalse();
            File.Exists(source.Path + ".quarantine").ShouldBeFalse();
            File.Exists(output.Path).ShouldBeFalse();
        }
        finally { Directory.Delete(output.Path + ".quarantine"); }
    }

    [Fact]
    public async Task EncodeAsync_GameCube_CreatesRawArtifactMetadataAndSeparateChildEstablishesEvidence()
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        if (Environment.GetEnvironmentVariable("ROMD_MEDIA_GC_RECOVERY_FIXTURE_EXPORT") is { } export)
        {
            Path.IsPathFullyQualified(export).ShouldBeTrue("fixture export requires an absolute new output file");
            await using var output = new FileStream(export, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous);
            await output.WriteAsync(original, CancellationToken.None);
            await output.FlushAsync(CancellationToken.None);
        }
        await using var source = child.Source(original);
        using var verifier = child.Verifier();
        var encoded = await new RvzEncodingService(verifier, Files(), child.Options).EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube);
        encoded.IsError.ShouldBeFalse(encoded.IsError ? encoded.FirstError.Description : null);
        await using var artifact = encoded.Value.Artifact;
        byte[] raw = await File.ReadAllBytesAsync(artifact.Path);
        artifact.DatHashes.ShouldBeNull();
        encoded.Value.Measurement.ShouldBe(new TempFileArtifactMeasurement(Sha256.FromSpan(SHA256.HashData(raw)),
            Md5.FromSpan(MD5.HashData(raw)), Sha1.FromSpan(SHA1.HashData(raw)), raw.Length));
        encoded.Value.RecipeVersion.ShouldBe(RvzCanonicalRecipe.Id);
        encoded.Value.EncoderBuild.ShouldBe(RvzEncoder.EncoderBuild);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        File.Exists(artifact.Path + ".quarantine").ShouldBeFalse();
        int encoderPid = int.Parse(await File.ReadAllTextAsync(source.Path + ".pid"));
        var proof = await verifier.VerifyAsync(artifact);
        proof.IsError.ShouldBeFalse(proof.IsError ? proof.FirstError.Description : null);
        proof.Value.Verification.ContentSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(original)));
        proof.Value.Verification.ContentSize.ShouldBe(original.Length);
        int verifierPid = int.Parse(await File.ReadAllTextAsync(artifact.Path + ".pid"));
        verifierPid.ShouldNotBe(encoderPid);
        MediaWorkerTestFixture.IsRunning(encoderPid).ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(verifierPid).ShouldBeFalse();
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public async Task EncodeAsync_WiiIsoAndRvz_UsesWiiBoundBuildAndSeparateFreshProof(byte index)
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(index), sectors: 5, poisonHashes: true).Iso;
        await using var source = child.Source(original, "wii-source");
        var capture = new CapturingSupervisor(child.Supervisor());
        using var verifier = new RvzVerifier(child.Options, capture);
        var service = new RvzEncodingService(verifier, Files(), child.Options);
        var first = await service.EncodeAsync(source, original.Length, RomIsoDiscKind.Wii);
        first.IsError.ShouldBeFalse(first.IsError ? first.FirstError.Description : null);
        await using var artifact = first.Value.Artifact;
        int firstEncoder = capture.EncodingResult!.Execution!.ProcessId;
        capture.EncodingResult.Reply!.Encoding!.EncoderBuild.ShouldBe(RvzWiiEncoder.EncoderBuild);
        byte[] raw = await File.ReadAllBytesAsync(artifact.Path);
        first.Value.Measurement.Sha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(raw)));
        first.Value.Measurement.Md5.ShouldBe(Md5.FromSpan(MD5.HashData(raw)));
        first.Value.Measurement.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(raw)));
        first.Value.Measurement.Size.ShouldBe(raw.Length);
        artifact.DatHashes.ShouldBeNull();
        var proof = await verifier.VerifyAsync(artifact);
        proof.IsError.ShouldBeFalse(proof.IsError ? proof.FirstError.Description : null);
        proof.Value.Verification.ContentSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(original)));
        proof.Value.Verification.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(original)));
        proof.Value.Verification.Md5.ShouldBe(Md5.FromSpan(MD5.HashData(original)));
        proof.Value.Verification.Crc32.ShouldBe(Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(original)));
        proof.Value.Verification.ContentSize.ShouldBe(original.Length);
        int proofPid = int.Parse(await File.ReadAllTextAsync(artifact.Path + ".pid"));
        proofPid.ShouldNotBe(firstEncoder);
        var second = await service.EncodeAsync(artifact, original.Length, RomIsoDiscKind.Wii);
        second.IsError.ShouldBeFalse(second.IsError ? second.FirstError.Description : null);
        await using var reencoded = second.Value.Artifact;
        int secondEncoder = capture.EncodingResult!.Execution!.ProcessId;
        secondEncoder.ShouldNotBe(firstEncoder);
        second.Value.EncoderBuild.ShouldBe(RvzWiiEncoder.EncoderBuild);
        (await File.ReadAllBytesAsync(reencoded.Path)).AsSpan().SequenceEqual(raw).ShouldBeTrue("Wii RVZ re-encoding must remain deterministic without dumping key-bearing artifacts");
        var secondProof = await verifier.VerifyAsync(reencoded);
        secondProof.IsError.ShouldBeFalse(secondProof.IsError ? secondProof.FirstError.Description : null);
        secondProof.Value.Verification.ContentSha256.ShouldBe(proof.Value.Verification.ContentSha256);
        int secondProofPid = int.Parse(await File.ReadAllTextAsync(reencoded.Path + ".pid"));
        secondProofPid.ShouldNotBe(secondEncoder);
        foreach (int pid in new[] { firstEncoder, proofPid, secondEncoder, secondProofPid })
            MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_RequestedKindMismatch_FailsWithoutOutputAndSameAdmissionAcceptsCorrectKind(bool sourceIsWii)
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = sourceIsWii ? SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(0), sectors: 1).Iso : SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original);
        using var verifier = child.Verifier();
        var service = new RvzEncodingService(verifier, Files(), child.Options);
        var wrong = await service.EncodeAsync(source, original.Length, sourceIsWii ? RomIsoDiscKind.GameCube : RomIsoDiscKind.Wii);
        wrong.FirstError.Code.ShouldBe("Rom.Media.Unsupported");
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
        await using var correct = (await service.EncodeAsync(source, original.Length, sourceIsWii ? RomIsoDiscKind.Wii : RomIsoDiscKind.GameCube)).Value.Artifact;
        (await verifier.VerifyAsync(correct)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_WiiBoundMinusOne_RejectsBeforeOutputAndEndsBothCustodies()
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(1), sectors: 5, poisonHashes: true).Iso;
        await using var source = child.Source(original);
        long cap = ((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetWiiOutputBound(original.Length, child.Options.MaxLogicalBytes)).MaximumOutputBytes;
        await using var output = (await Files().ReserveOutputAsync(cap - 1)).Value;
        var failed = await child.Supervisor().EncodeAsync(source, output, original.Length, RomIsoDiscKind.Wii);
        failed.Failure.ShouldBe(MediaWorkerFailure.LimitExceeded);
        File.Exists(output.Path).ShouldBeFalse();
        File.Exists(output.Path + ".quarantine").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
    }

    [Theory]
    [InlineData("crash", "Crash")]
    [InlineData("encode-wrong-build", "Protocol")]
    [InlineData("encode-cross-kind-build", "Protocol")]
    [InlineData("encode-wrong-output", "Protocol")]
    public async Task EncodeAsync_WiiPoisonChild_AllowsSiblingThroughSameFailedVerifier(string mode, string failure)
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(0), sectors: 1).Iso;
        await using var source = child.Source(original, mode);
        using var verifier = child.Verifier();
        var service = new RvzEncodingService(verifier, Files(), child.Options);
        (await service.EncodeAsync(source, original.Length, RomIsoDiscKind.Wii)).FirstError.Code.ShouldBe("Rom.Media." + failure);
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        await using var sibling = child.Source(original, "wii-sibling");
        await using var artifact = (await service.EncodeAsync(sibling, original.Length, RomIsoDiscKind.Wii)).Value.Artifact;
        (await verifier.VerifyAsync(artifact)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_InvalidDiscKind_RejectsBeforeAdmissionReservationOrLaunch()
    {
        using var child = new MediaWorkerTestFixture();
        await using var source = child.Source(SyntheticNintendoIso.GameCube());
        using var verifier = child.Verifier();
        (await new RvzEncodingService(verifier, Files(), child.Options).EncodeAsync(source, source.FileSize, (RomIsoDiscKind)99))
            .FirstError.Code.ShouldBe("Rom.Media.Unsupported");
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_WiiDeadlineOrCancellation_ReapsAndAllowsSiblingThroughSameVerifier(bool cancel)
    {
        // Every run through this verifier gets its configured wall budget, so it must also cover the sibling's real encode under full-suite load;
        // the hanging child reaches the deadline whatever the budget, so both cases keep the fixture's 10 s default.
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(1), sectors: 1).Iso;
        await using var source = child.Source(original, "descendant-hang");
        using var verifier = child.Verifier();
        var service = new RvzEncodingService(verifier, Files(), child.Options);
        using var cancellation = new CancellationTokenSource();
        var running = service.EncodeAsync(source, original.Length, RomIsoDiscKind.Wii, cancellation.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int parent = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
        int descendant = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".descendant.pid", deadline.Token);
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).Length.ShouldBe(1);
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
        if (cancel)
        {
            await cancellation.CancelAsync();
            OperationCanceledException? observed = null;
            try
            { await running; }
            catch (OperationCanceledException error) { observed = error; }
            observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            (await running).FirstError.Code.ShouldBe("Rom.Media.WallDeadline");
        MediaWorkerTestFixture.IsRunning(parent).ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(descendant).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        await using var sibling = child.Source(original, "wii-sibling");
        var recovered = await service.EncodeAsync(sibling, original.Length, RomIsoDiscKind.Wii);
        recovered.IsError.ShouldBeFalse("the same verifier must encode the sibling; failure=" + (recovered.IsError ? recovered.FirstError.Code : "none"));
        await using var artifact = recovered.Value.Artifact;
        (await verifier.VerifyAsync(artifact)).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wii")]
    public async Task EncodeAsync_UnsupportedContent_CreatesNoArtifactAndSiblingCanProceed(string kind)
    {
        using var child = new MediaWorkerTestFixture();
        byte[] unsupported = kind == "wii" ? SyntheticNintendoIso.Wii(0) : new byte[0x6000];
        await using var source = child.Source(unsupported);
        using var verifier = child.Verifier();
        var service = new RvzEncodingService(verifier, Files(), child.Options);
        var rejected = await service.EncodeAsync(source, unsupported.Length, RomIsoDiscKind.GameCube);
        rejected.FirstError.Code.ShouldBe("Rom.Media.Unsupported");
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
        await using var sibling = child.Source(SyntheticNintendoIso.GameCube(), "sibling");
        await using var artifact = (await service.EncodeAsync(sibling, sibling.FileSize, RomIsoDiscKind.GameCube)).Value.Artifact;
    }

    [Theory]
    [InlineData("crash", "Crash")]
    [InlineData("malformed-output", "Protocol")]
    [InlineData("stdout-overflow", "OutputLimit")]
    [InlineData("diagnostic-overflow", "OutputLimit")]
    [InlineData("encode-wrong-source", "Protocol")]
    [InlineData("encode-wrong-length", "Protocol")]
    [InlineData("encode-wrong-output", "Protocol")]
    [InlineData("encode-wrong-build", "Protocol")]
    [InlineData("encode-cross-kind-build", "Protocol")]
    [InlineData("encode-normal-priority", "Protocol")]
    [InlineData("encode-truncated", "Protocol")]
    [InlineData("encode-success-nonzero", "Crash")]
    [InlineData("descendant-exit", "Crash")]
    public async Task EncodeAsync_PoisonChild_ReapsBothCustodiesAndSharesRecoveryCapacity(string mode, string failure)
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original, mode);
        using var verifier = child.Verifier();
        var service = new RvzEncodingService(verifier, Files(), child.Options);
        var result = await service.EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube);
        result.FirstError.Code.ShouldBe("Rom.Media." + failure);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        int pid = int.Parse(await File.ReadAllTextAsync(source.Path + ".pid"));
        MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
        if (mode == "descendant-exit")
            MediaWorkerTestFixture.IsRunning(int.Parse(await File.ReadAllTextAsync(source.Path + ".descendant.pid"))).ShouldBeFalse();
        await using var sibling = child.Source(original, "sibling");
        await using var artifact = (await service.EncodeAsync(sibling, original.Length, RomIsoDiscKind.GameCube)).Value.Artifact;
        (await verifier.VerifyAsync(artifact)).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_RunningChild_HoldsInspectionAdmissionUntilDeadlineOrCancellationAndJoinsDescendants(bool cancel)
    {
        using var child = new MediaWorkerTestFixture();
        child.Options.WallTimeoutSeconds = cancel ? 10 : 4;
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original, "descendant-hang");
        using var verifier = child.Verifier();
        var service = new RvzEncodingService(verifier, Files(), child.Options);
        using var cancellation = new CancellationTokenSource();
        Task<ErrorOr.ErrorOr<RvzEncodedArtifact>> running = service.EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube, cancellation.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int pid = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
        int descendant = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".descendant.pid", deadline.Token);
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).Length.ShouldBe(1);
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
        if (cancel)
        {
            await cancellation.CancelAsync();
            OperationCanceledException? observed = null;
            try
            { await running; }
            catch (OperationCanceledException error) { observed = error; }
            observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            (await running).FirstError.Code.ShouldBe("Rom.Media.WallDeadline");
        MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(descendant).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        using var recovered = await verifier.AcquireAsync(default);
    }

    [Fact]
    public async Task EncodeAsync_InspectionOccupiesSlot_ReservesNoOutputAndStartsNoEncodingChild()
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = child.Source(original);
        using var verifier = child.Verifier();
        using var admittedInspection = await verifier.AcquireAsync(default);
        await Should.ThrowAsync<ResourceCapacityException>(() => new RvzEncodingService(verifier, Files(), child.Options).EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube));
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_UnconfirmedContainment_RetainsBothActualLeasesAndMediaCapacity(bool cancel)
    {
        using var child = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        var files = Files(slots: 2);
        var source = await files.CreateAsync(new MemoryStream(original));
        using var cancellation = new CancellationTokenSource();
        var supervisor = new MediaWorkerSupervisor(child.Options, () => new MediaWorkerProcessLifetime((_, _) =>
        {
            if (cancel)
                cancellation.Cancel();
            throw new MediaWorkerCleanupException("fixture containment observation failed");
        }));
        using var verifier = new RvzVerifier(child.Options, supervisor);
        var service = new RvzEncodingService(verifier, files, child.Options);
        try
        {
            if (cancel)
            {
                OperationCanceledException? observed = null;
                try
                { await service.EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube, cancellation.Token); }
                catch (OperationCanceledException error) { observed = error; }
                observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            }
            else
                (await service.EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube)).FirstError.Code.ShouldBe("Rom.Media.Containment");
        }
        finally { await source.DisposeAsync(); }
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).Length.ShouldBe(2);
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).Length.ShouldBe(2);
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
        await Should.ThrowAsync<ResourceCapacityException>(() => Files(slots: 2).ReserveOutputAsync(1));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unrequested-cancellation")]
    [InlineData("requested-cancellation")]
    [InlineData("quarantine-io")]
    public async Task EncodeAsync_UnexpectedSupervisorThrow_QuarantinesBothActualLeasesAndNeverReusesReservation(string failure)
    {
        byte[] original = SyntheticNintendoIso.GameCube();
        var files = Files(slots: 2);
        var actual = await files.CreateAsync(new MemoryStream(original));
        ITempFile source = failure == "quarantine-io" ? new ThrowingQuarantineInput(actual) : actual;
        using var cancellation = new CancellationTokenSource();
        var supervisor = new UnknownStopSupervisor(failure, cancellation);
        var options = new MediaProcessingOptions { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 };
        using var verifier = new RvzVerifier(options, supervisor);
        var service = new RvzEncodingService(verifier, files, options);
        try
        {
            if (failure == "requested-cancellation")
            {
                OperationCanceledException? observed = null;
                try
                { await service.EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube, cancellation.Token); }
                catch (OperationCanceledException error) { observed = error; }
                observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            }
            else
            {
                var result = await service.EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube);
                result.IsError.ShouldBeTrue();
                result.FirstError.Code.ShouldBe("Rom.Media.Containment");
                result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
            }
            source.TryEndChildUse().ShouldBeFalse("unknown stop must permanently quarantine the source");
        }
        finally { await source.DisposeAsync(); }
        supervisor.Calls.ShouldBe(1);
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).Length.ShouldBe(2);
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).Length.ShouldBe(2);
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
        await Should.ThrowAsync<ResourceCapacityException>(() => files.ReserveOutputAsync(1));
    }

    [Fact]
    public async Task EncodeAsync_CanceledBeforeReservationRuns_DoesNotLaunchOrQuarantineAndReleasesUnstartedSlot()
    {
        var files = Files(slots: 2);
        await using var source = await files.CreateAsync(new MemoryStream(SyntheticNintendoIso.GameCube()));
        await using var output = (await files.ReserveOutputAsync(1 << 20)).Value;
        using var cancellation = new CancellationTokenSource();
        var supervisor = new UnknownStopSupervisor("io", cancellation);
        using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 }, supervisor);
        using var admission = await verifier.AcquireAsync(default);
        await cancellation.CancelAsync();
        OperationCanceledException? observed = null;
        try
        { await admission.EncodeAsync(source, output, source.FileSize, RomIsoDiscKind.GameCube, cancellation.Token); }
        catch (OperationCanceledException error) { observed = error; }
        observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        supervisor.Calls.ShouldBe(0);
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        using var recovered = await verifier.AcquireAsync(default);
    }

    [Fact]
    public async Task EncodeAsync_CertifiedStoppedCancellation_PreservesExceptionAndReclaimsOutputAndAdmission()
    {
        byte[] original = SyntheticNintendoIso.GameCube();
        var files = Files(slots: 2);
        await using var source = await files.CreateAsync(new MemoryStream(original));
        using var cancellation = new CancellationTokenSource();
        var supervisor = new UnknownStopSupervisor("certified-cancellation", cancellation);
        var options = new MediaProcessingOptions { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 };
        using var verifier = new RvzVerifier(options, supervisor);
        var service = new RvzEncodingService(verifier, files, options);
        OperationCanceledException? observed = null;
        try
        { await service.EncodeAsync(source, original.Length, RomIsoDiscKind.GameCube, cancellation.Token); }
        catch (OperationCanceledException error) { observed = error; }
        observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        observed.ShouldBeSameAs(supervisor.CertifiedCancellation);
        supervisor.Calls.ShouldBe(1);
        Directory.GetFiles(_root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBe([source.Path]);
        using var recovered = await verifier.AcquireAsync(default);
        await using var reusable = (await files.ReserveOutputAsync(1)).Value;
    }

    private sealed class CapturingSupervisor(MediaWorkerSupervisor inner) : IMediaWorkerSupervisor
    {
        internal MediaWorkerResult? EncodingResult { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => inner.HandshakeAsync(ct);
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => inner.InspectAsync(source, ct);
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => inner.InspectIsoAsync(source, ct);
        public async Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default)
        {
            EncodingResult = await inner.EncodeAsync(source, output, logicalBytes, discKind, ct);
            return EncodingResult;
        }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
