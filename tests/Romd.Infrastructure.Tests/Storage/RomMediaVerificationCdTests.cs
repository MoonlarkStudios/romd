using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Saturn;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomMediaVerificationCdTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-retained-cd-").FullName;
    [LinuxEncodingTheory]
    [InlineData(false, 0, "valid", true)]
    [InlineData(true, 150, "valid", true)]
    [InlineData(true, 0, "new-boot", true)]
    [InlineData(true, 0, "wrong-family", true)]
    [InlineData(true, 0, "wrong-family")]
    [InlineData(false, 0, "valid")]
    [InlineData(true, 150, "valid")]
    [InlineData(true, 0, "new-boot")]
    [InlineData(true, 0, "absent-boot")]
    [InlineData(true, 0, "absent-boot-member")]
    [InlineData(true, 0, "absent-boot-cue")]
    [InlineData(true, 0, "member")]
    [InlineData(true, 0, "cue")]
    [InlineData(true, 0, "boot")]
    [InlineData(true, 0, "decoder")]
    [InlineData(true, 0, "custody-return")]
    [InlineData(true, 0, "custody-throw")]
    [InlineData(true, 0, "custody-cancel")]
    [InlineData(true, 0, "stopped-cancel")]
    public async Task VerifyAsync_RetainedArchiveWithoutOriginalBins_MeasuresWholeMembersAndRejectsContradictions(bool multipleBins, int pregap, string mutation, bool saturn = false)
    {
        using var fixture = new MediaWorkerTestFixture();
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(2L << 30);
        var factory = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions { MaximumFiles = 32, MaximumBytes = 4L << 30, MinimumFreeBytes = 0 });
        var runtime = new MediaProcessingRuntime(options, fixture.Options);
        var supervisor = new MediaWorkerSupervisor(fixture.Options, () => new MediaWorkerProcessLifetime(), runtime: runtime,
            chdCandidate: ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory));
        var tool = ChdmanCandidate.FromBuildRecord(NativeTestBuilds.ChdmanDirectory);
        bool absentBoot = mutation.StartsWith("absent-boot", StringComparison.Ordinal);
        byte[] image = saturn ? SyntheticSaturnCd.Image() : SyntheticPs1Cd.Image();
        if (absentBoot)
            image.AsSpan(26 * 2048, 8).Clear(); // Exact bytes remain reconstructible, without a recognized executable.
        byte[] data = SyntheticPs1Cd.ToRaw(image, pregap: pregap);
        byte[] audio = Enumerable.Range(0, 7 * 2352).Select(i => (byte)(i * 13)).ToArray();
        int frames = data.Length / 2352;
        string Time(int frame) => $"00:{frame / 75:00}:{frame % 75:00}";
        string firstGap = pregap == 0 ? " INDEX 01 00:00:00\n" : $" INDEX 00 00:00:00\n INDEX 01 {Time(pregap)}\n";
        string text = "FILE \"data.bin\" BINARY\n TRACK 01 MODE1/2352\n" + firstGap +
            (multipleBins ? "FILE \"audio.bin\" BINARY\n" : "") + " TRACK 02 AUDIO\n" +
            $" INDEX 00 {Time(multipleBins ? 0 : frames)}\n INDEX 01 {Time((multipleBins ? 0 : frames) + 2)}\n";
        byte[] cueBytes = Encoding.UTF8.GetBytes(text), sidecarBytes = [0x53, 0x42, 0x49, 0, 0xfe];
        List<ITempFile> originals = [];
        async Task<ITempFile> Stage(byte[] bytes)
        { var staged = await factory.CreateAsync(new MemoryStream(bytes)); originals.Add(staged); return staged; }
        var cue = await Stage(cueBytes);
        var bins = new Dictionary<string, ITempFile> { ["data.bin"] = await Stage(multipleBins ? data : [.. data, .. audio]) };
        if (multipleBins)
            bins.Add("audio.bin", await Stage(audio));
        var sidecar = await Stage(sidecarBytes);
        var workflow = new ChdCdArchiveWorkflow(factory, supervisor, TimeProvider.System);
        await using var prepared = await workflow.PrepareAsync("disc.cue", cue, bins, new Dictionary<string, ITempFile> { ["disc.sbi"] = sidecar },
            requiredBootProfile: absentBoot ? null : saturn != (mutation == "wrong-family") ? SaturnCdIdentifier.Profile : Ps1CdIdentifier.Profile);
        if (mutation == "wrong-family")
        {
            await Should.ThrowAsync<RomCdWorkerFailureException>(() => workflow.EncodeAsync(prepared, tool, 2 << 20));
            workflow.ProcessTreeStopped.ShouldBeTrue();
            foreach (var original in originals)
                await original.DisposeAsync();
            return;
        }
        await using var encoded = await workflow.EncodeAsync(prepared, tool, 2 << 20);
        var observed = encoded.VerificationRun.Reply!.CdVerification!.ObservedBoot;
        (observed.Profile is null).ShouldBe(absentBoot);
        encoded.VerificationRun.Reply.CdInspection!.ObservedBoot.ShouldBe(observed);
        if (mutation is "valid" or "new-boot" or "absent-boot")
        {
            // This child receives only the CHD, with no source request, original CUE/BINs or catalog.
            var raw = await supervisor.InspectChdCdAsync(encoded.Artifact);
            raw.IsSuccess.ShouldBeTrue(raw.Failure?.ToString());
            raw.ProcessTreeStopped.ShouldBeTrue();
            raw.Reply!.CdVerification.ShouldBeNull();
            raw.Reply.CdInspection!.ObservedBoot.ShouldBe(observed);
            MediaWorkerTestFixture.IsRunning(raw.Execution!.ProcessId).ShouldBeFalse();
        }
        var measured = encoded.Measured;
        var layout = measured.Manifest.ShouldBeOfType<RomReconstructionManifest.CdTracks>();
        var boot = RomCdMeasurementMapper.MapBoot(observed);
        if (mutation == "boot")
        {
            var positive = boot.ShouldBeOfType<RomPs1CdBoot>();
            boot = new RomPs1CdBoot(positive.Profile, positive.FirstTrackIndex01, positive.VolumeBlocks, positive.RootBlock, positive.RootBytes, "OTHER.EXE;1", positive.BootBlock, positive.BootBytes, positive.EntryPoint, positive.LoadAddress, positive.LoadBytes);
        }
        var members = layout.Members.ToArray();
        if (mutation.EndsWith("member", StringComparison.Ordinal))
        {
            var bin = members.OfType<RomCdBinMember>().Last();
            var wrong = new RomCdFileIdentity(bin.Identity.Size, Sha256.Parse(new string('1', 64)), bin.Identity.Sha1, bin.Identity.Md5, bin.Identity.Crc32);
            members[Array.IndexOf(members, bin)] = new RomCdBinMember(bin.Path, wrong, wrong, bin.Tracks);
        }
        var manifest = new RomReconstructionManifest.CdTracks(layout.LogicalFrames, layout.Tracks, members);
        var outputs = members.Select((member, ordinal) => (member, ordinal)).Where(item => item.member is RomCdBinMember)
            .Select((item, index) => new RomRepresentationOutput(101 + index, index + 1, new RomOutputSelector.Member(item.ordinal),
                new(item.member.Identity.Size, item.member.Identity.Sha256, item.member.Identity.Sha1, item.member.Identity.Md5, item.member.Identity.Crc32))).ToArray();
        // A contradictory fixture still describes a complete claimed measurement. The fresh decoder must independently refute it.
        var prior = measured.Measurement;
        var claimed = new RomRepresentationVerification(prior.Id, prior.ArtifactSha256, prior.ArtifactSize, prior.Decoder, prior.CheckedProfile,
            prior.VerifierBuild, prior.ObservedAt, outputs.Select(output => new RomMeasuredOutput(output.Selector, output.Identity)).ToArray(),
            measured.Container, prior.ComputedRawSha1, prior.ComputedOverallSha1);
        var descriptors = members.Select((member, ordinal) => (member, ordinal)).Where(item => item.member is RomCdStoredMember)
            .Select(item => new RomRetainedDescriptor(item.ordinal, item.member.Path == "disc.cue" ? 3 : 4, item.member.Identity.Sha256, item.member.Identity.Size)).ToArray();
        var facts = new RomRepresentationFacts(42, 9, prior.ArtifactSha256, prior.ArtifactSize, measured.Container, manifest, outputs, [claimed], descriptors,
            cdBootInspections: mutation == "new-boot" || boot is null ? [] : [new(Guid.NewGuid(), claimed.Id, MediaInspectionService.DecoderBuild, prior.ObservedAt, boot)]);
        byte[] artifactBytes = await File.ReadAllBytesAsync(encoded.Artifact.Path);
        await prepared.DisposeAsync();
        foreach (var original in originals)
            await original.DisposeAsync();
        originals.All(original => !File.Exists(original.Path)).ShouldBeTrue();
        var catalog = Substitute.For<IMediaFactReader>();
        catalog.ReadAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 42 })), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomRepresentationFacts> { [42] = facts });
        var files = Substitute.For<IFileStorageService>();
        files.RetrieveByIdAsync(9, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(artifactBytes));
        files.RetrieveByIdAsync(3, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(mutation.EndsWith("cue", StringComparison.Ordinal) ? [.. cueBytes[..^1], (byte)'x'] : cueBytes));
        files.RetrieveByIdAsync(4, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(sidecarBytes));
        var requestedDecoder = mutation == "decoder" ? new RomDecoderIdentity.Native(new(
            ((RomDecoderIdentity.Native)prior.Decoder).Build.Rid, ((RomDecoderIdentity.Native)prior.Decoder).Build.UpstreamCommit,
            ((RomDecoderIdentity.Native)prior.Decoder).Build.RecipeVersion + 1)) : prior.Decoder;
        var selected = new RomMediaVerificationSource(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, requestedDecoder);
        var uncertain = Substitute.For<IMediaWorkerSupervisor>();
        using var cancellation = new CancellationTokenSource();
        var stoppedCancellation = new MediaWorkerStoppedCancellationException(cancellation.Token);
        MediaProcessingRuntime.MediaProcessingReservation? observedRun = null;
        uncertain.ReverifyChdCdAsync(Arg.Any<ITempFile>(), Arg.Any<ITempFile>(), Arg.Any<IReadOnlyList<ITempFile>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                observedRun = MediaProcessingRuntime.Current;
                if (mutation == "custody-cancel")
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                return mutation switch
                {
                    "custody-return" => Task.FromResult(new MediaWorkerResult(null, MediaWorkerFailure.Containment, null, false)),
                    "custody-throw" => throw new IOException("Injected loss of stop observation."),
                    "stopped-cancel" => throw stoppedCancellation,
                    _ => throw new InvalidOperationException("Unexpected synthetic supervisor call.")
                };
            });
        IMediaWorkerSupervisor verificationSupervisor = mutation.StartsWith("custody-", StringComparison.Ordinal) || mutation == "stopped-cancel" ? uncertain : supervisor;
        var media = new RomMediaVerification(factory, catalog, files, runtime, fixture.Options,
            () => throw new InvalidOperationException("No whole-CHD fallback."),
            () => throw new InvalidOperationException("No RVZ fallback."), new(factory, files, verificationSupervisor, runtime, TimeProvider.System));
        var acquired = await media.AcquireAsync(Guid.NewGuid(), selected, default);
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;
        if (mutation == "stopped-cancel")
        {
            OperationCanceledException? canceled = null;
            try
            { await lease.VerifyAsync(cancellation.Token); }
            catch (OperationCanceledException error) { canceled = error; }
            canceled.ShouldNotBeNull().ShouldBeSameAs(stoppedCancellation);
            lease.ProcessTreeStopped.ShouldBeTrue();
            (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
            using var recovered = await runtime.AcquireAsync();
            return;
        }
        ErrorOr<RomMediaVerificationMeasurement> result = default;
        OperationCanceledException? uncertainCancellation = null;
        try
        { result = await lease.VerifyAsync(cancellation.Token); }
        catch (OperationCanceledException error) when (mutation == "custody-cancel") { uncertainCancellation = error; }
        if (mutation.StartsWith("custody-", StringComparison.Ordinal))
        {
            if (mutation == "custody-cancel")
                uncertainCancellation.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            else
                result.FirstError.Code.ShouldBe(RomMediaVerificationErrors.Custody.Code);
            lease.ProcessTreeStopped.ShouldBeFalse();
            (await lease.ReleaseProcessingAsync()).FirstError.Code.ShouldBe(RomMediaVerificationErrors.Custody.Code);
            await lease.DisposeAsync();
            await Should.ThrowAsync<ResourceCapacityException>(() => runtime.AcquireAsync());
            var run = observedRun.ShouldNotBeNull();
            string[] markers = Directory.EnumerateFiles(_root, "*.quarantine", SearchOption.AllDirectories).ToArray();
            markers.Length.ShouldBe(facts.Descriptors.Count + 2); // Artifact, stored descriptors and bounded request.
            foreach (string path in markers)
            {
                new FileInfo(path).Length.ShouldBeGreaterThan(0L);
                using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
                marker.RootElement.EnumerateObject().Count().ShouldBe(3);
                marker.RootElement.GetProperty("Version").GetString().ShouldBe("romd-media-hold/v1");
                marker.RootElement.GetProperty("RunId").GetString().ShouldBe(run.Nonce);
                marker.RootElement.GetProperty("RecordPath").GetString().ShouldBe(run.RecordPath);
                File.Exists(path[..^".quarantine".Length]).ShouldBeTrue();
            }
            return;
        }
        lease.ProcessTreeStopped.ShouldBeTrue();
        result.IsError.ShouldBe(mutation is not ("valid" or "new-boot" or "absent-boot"), result.IsError ? result.FirstError.ToString() : "unexpected proof");
        if (!result.IsError)
        {
            var proof = result.Value.ShouldBeOfType<RomMediaVerificationMeasurement.Cd>();
            proof.Representation.MatchesRetained(facts).ShouldBeTrue();
            proof.Representation.Measurement.Outputs.Count.ShouldBe(outputs.Length);
            proof.Representation.Measurement.Id.ShouldNotBe(claimed.Id);
            proof.Boot.ShouldBe(boot);
        }
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        await files.DidNotReceive().RetrieveByIdAsync(1, Arg.Any<CancellationToken>());
        await files.DidNotReceive().RetrieveByIdAsync(2, Arg.Any<CancellationToken>());
    }
    public void Dispose() => Directory.Delete(_root, true);
}
