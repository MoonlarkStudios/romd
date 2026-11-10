using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.TestSupport;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Actual staging and supervised codecs over synthetic public fixtures. No private corpus or deployed-image qualification.</summary>
[Collection(NativeMediaCodecWorkloads.Name)]
public sealed partial class MediaReconciliationEncodingTests(ITestOutputHelper testOutput)
{
    [LinuxEncodingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encode_OriginalAndRetainedPsp_UsesWholeFileChdPathWithIndependentExactReconstruction(bool modern)
    {
        await using var f = CreateFixture(true, null, null);
        var source = f.Original(SyntheticPspUmd.CodecImage(modern), "disc.iso", MediaReconciliationSourceRole.Whole, 1);
        var attempt = f.Attempt("psp-umd", new([source], null, [], null));
        attempt.Capture.DiscKind.ShouldBe(RomIsoDiscKind.PlayStationPortableUmd);
        var acquired = await f.Encoding(f.Profile).AcquireAsync(attempt);
        acquired.IsError.ShouldBeFalse();
        MediaReconciliationInput.Logical retained;
        await using (var lease = acquired.Value)
        {
            var result = await lease.EncodeAndVerifyAsync(_ => Task.CompletedTask);
            result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.ToString() : null);
            var encoded = result.Value.ShouldBeOfType<MediaReconciliationEncoded.Whole>();
            encoded.Measurement.Inspection.Layout.ShouldBeOfType<RomPspLayoutFacts>().DiscId.ShouldBe("ULUS12345");
            encoded.SourceMeasurement.Inspection.Layout.ShouldBe(encoded.Measurement.Inspection.Layout);
            encoded.Measured.Measurement.Outputs.ShouldHaveSingleItem().Identity.ShouldBe(source.Output.Identity);
            retained = await f.RetainAsync(encoded, "retained.chd", 2, 1);
            (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        }
        // The second attempt must independently inspect and extract the retained CHD; the original is unavailable.
        f.Payloads.Remove(1);
        f.Files.ClearReceivedCalls();
        var again = await f.Encoding(f.Profile).AcquireAsync(f.Attempt("psp-umd", new([retained], null, [], null)));
        again.IsError.ShouldBeFalse();
        await using var second = again.Value;
        var reencoded = await second.EncodeAndVerifyAsync(_ => Task.CompletedTask);
        reencoded.IsError.ShouldBeFalse(reencoded.IsError ? reencoded.FirstError.ToString() : null);
        var output = reencoded.Value.ShouldBeOfType<MediaReconciliationEncoded.Whole>();
        output.Measured.Measurement.Outputs.ShouldHaveSingleItem().Identity.ShouldBe(source.Output.Identity);
        output.SourceMeasurement.Inspection.Layout.ShouldBe(output.Measurement.Inspection.Layout);
        second.ProcessTreeStopped.ShouldBeTrue();
        (await second.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        await f.Files.DidNotReceive().RetrieveByIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Encode_OriginalNintendo_InvokesRealEncoderAndIndependentDecoderWhileRetainingPayloadUntilDisposal()
    {
        await using var f = CreateFixture(false, null, null);
        var source = f.Original(SyntheticNintendoIso.GameCube(), "disc.iso", MediaReconciliationSourceRole.Whole, 1);
        var attempt = f.Attempt("gamecube-disc", new([source], null, [], null));
        var acquired = await f.Encoding(f.Profile).AcquireAsync(attempt);
        acquired.IsError.ShouldBeFalse();
        var lease = acquired.Value;
        int verifications = 0;
        var result = await lease.EncodeAndVerifyAsync(_ => { verifications++; return Task.CompletedTask; });
        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.ToString() : null);
        lease.ProcessTreeStopped.ShouldBeTrue();
        verifications.ShouldBe(1);
        var output = result.Value.ShouldBeOfType<MediaReconciliationEncoded.Whole>();
        output.Recipe.ShouldBe(attempt.Capture.Identity.Recipe);
        output.EncoderBuild.ShouldBe(attempt.Capture.EncoderBuild);
        output.Measured.Measurement.Decoder.ShouldBe(attempt.Capture.Decoder);
        output.Measured.Measurement.Outputs.Single().Identity.ShouldBe(source.Output.Identity);
        output.SourceMeasurement.Representation.MatchesRetained(source.Graph).ShouldBeTrue();
        output.SourceMeasurement.Representation.Measurement.Decoder.ShouldBe(f.Profile.OriginalInspectionDecoder);
        output.SourceMeasurement.Inspection.Identity.ShouldBe(source.Output.Identity);
        output.Measurement.Inspection.Layout.ShouldBeOfType<RomNintendoLayoutFacts>();
        source.Graph.Verifications.ShouldHaveSingleItem();
        File.Exists(output.Artifact.Path).ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => lease.EncodeAndVerifyAsync(_ => Task.CompletedTask));
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        File.Exists(output.Artifact.Path).ShouldBeTrue();
        using (await f.Runtime.AcquireAsync())
        { }
        await lease.DisposeAsync();
        File.Exists(output.Artifact.Path).ShouldBeFalse();
        await f.Files.DidNotReceiveWithAnyArgs().StageRawAsync(default!, default);
    }

    [Theory]
    [InlineData("encoder")]
    [InlineData("limits")]
    [InlineData("recipe")]
    public async Task Acquire_ChangedCapturedExecutionContext_RefusesBeforeProcessingAndPayloadAccess(string change)
    {
        await using var f = CreateFixture(false, null, null);
        var source = f.Original(SyntheticNintendoIso.GameCube(), "disc.iso", MediaReconciliationSourceRole.Whole, 1);
        var planned = f.Attempt("gamecube-disc", new([source], null, [], null));
        var c = planned.Capture;
        var changed = new MediaReconciliationCapture(c.JobId, c.RequestedVersion, c.Sources, c.Identity.Operation,
            change == "recipe" ? RecipeId.Parse("other-recipe/v2") : c.Identity.Recipe, Hash('a'), change == "limits" ? Hash('f') : c.Identity.LimitsFingerprint,
            c.Decoder, c.KindId, change == "encoder" ? "other-encoder-build" : c.EncoderBuild, c.DiscKind);
        (await f.Encoding(f.Profile).AcquireAsync(new(1, changed, null))).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        f.Files.ReceivedCalls().ShouldBeEmpty();
        using var capacity = await f.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Encode_PhysicalSourceChanged_RefusesBeforeInvokingTheIdentifierOrEncoder()
    {
        var identifier = Substitute.For<IRomOriginalMediaIdentifier>();
        await using var f = CreateFixture(false, null, identifier);
        var source = f.Original(SyntheticNintendoIso.GameCube(), "disc.iso", MediaReconciliationSourceRole.Whole, 1);
        f.Payloads[1] = new byte[checked((int)source.Graph.ArtifactSize)];
        var acquired = await f.Encoding(f.Profile).AcquireAsync(f.Attempt("gamecube-disc", new([source], null, [], null)));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;
        (await lease.EncodeAndVerifyAsync(_ => Task.CompletedTask)).IsError.ShouldBeTrue();
        identifier.ReceivedCalls().ShouldBeEmpty();
        lease.ProcessTreeStopped.ShouldBeTrue();
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task Encode_UnknownInspectionStop_RetainsEveryChargeAndExactRunQuarantineMarker(string failure)
    {
        var identifier = Substitute.For<IRomOriginalMediaIdentifier>();
        await using var f = CreateFixture(false, null, identifier);
        using var cancellation = new CancellationTokenSource();
        MediaProcessingRuntime.MediaProcessingReservation? run = null;
        identifier.IdentifyAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            run = MediaProcessingRuntime.Current;
            if (failure == "cancel")
            { cancellation.Cancel(); return Task.FromException<ErrorOr<RomOriginalMediaIdentification>>(new OperationCanceledException(cancellation.Token)); }
            return Task.FromException<ErrorOr<RomOriginalMediaIdentification>>(new IOException("unknown inspector stop"));
        });
        var source = f.Original(SyntheticNintendoIso.GameCube(), "disc.iso", MediaReconciliationSourceRole.Whole, 1);
        var attempt = f.Attempt("gamecube-disc", new([source], null, [], null));
        var acquired = await f.Encoding(f.Profile).AcquireAsync(attempt);
        acquired.IsError.ShouldBeFalse();
        var lease = acquired.Value;
        if (failure == "cancel")
            await Should.ThrowAsync<OperationCanceledException>(() => lease.EncodeAndVerifyAsync(_ => Task.CompletedTask, cancellation.Token));
        else
            (await lease.EncodeAndVerifyAsync(_ => Task.CompletedTask)).FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
        lease.ProcessTreeStopped.ShouldBeFalse();
        (await lease.ReleaseProcessingAsync()).FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
        run.ShouldNotBeNull();
        await lease.DisposeAsync();
        File.Exists(run.RecordPath).ShouldBeTrue();
        string[] holds = Directory.GetFiles(f.Root, "*.quarantine", SearchOption.AllDirectories);
        holds.ShouldNotBeEmpty();
        foreach (string hold in holds)
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(hold));
            json.RootElement.GetProperty("Version").GetString().ShouldBe("romd-media-hold/v1");
            json.RootElement.GetProperty("RunId").GetString().ShouldBe(run.Nonce);
            json.RootElement.GetProperty("RecordPath").GetString().ShouldBe(run.RecordPath);
        }
        await Should.ThrowAsync<ResourceCapacityException>(() => f.Runtime.AcquireAsync());
    }

    [LinuxEncodingTheory]
    [InlineData("valid")]
    [InlineData("deadline")]
    [InlineData("wall-deadline")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    [InlineData("stopped-cancel")]
    [InlineData("verify-cancel")]
    [InlineData("unknown")]
    public async Task Encode_CompleteCd_WithIdenticalBinSources_PreservesSelectorsDescriptorsAndStoppedOutcome(string result)
    {
        var injected = Substitute.For<IMediaWorkerSupervisor>();
        string[] heldPaths = [];
        var certifiedCancellation = new MediaWorkerStoppedCancellationException(CancellationToken.None);
        var checkpointCancellation = new OperationCanceledException("cancelled between stopped encoding and verification");
        injected.EncodeChdCdAsync(Arg.Any<ITempFile>(), Arg.Any<IReadOnlyList<ITempFile>>(), Arg.Any<IWritableTempFile>(), Arg.Any<ChdmanCandidate>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                heldPaths = call.Arg<IReadOnlyList<ITempFile>>().Select(file => file.Path)
                    .Append(call.ArgAt<ITempFile>(0).Path).Append(call.Arg<IWritableTempFile>().Path).ToArray();
                return result switch
                {
                    "deadline" => Task.FromResult(new MediaWorkerResult(null, MediaWorkerFailure.CpuDeadline, null, ProcessTreeStopped: true)),
                    "wall-deadline" => Task.FromResult(new MediaWorkerResult(null, MediaWorkerFailure.WallDeadline, null, ProcessTreeStopped: true)),
                    "malformed" => Task.FromResult(new MediaWorkerResult(null, MediaWorkerFailure.Malformed, null, ProcessTreeStopped: true)),
                    "unsupported" => Task.FromResult(new MediaWorkerResult(null, MediaWorkerFailure.Unsupported, null, ProcessTreeStopped: true)),
                    "stopped-cancel" => Task.FromException<MediaWorkerResult>(certifiedCancellation),
                    _ => Task.FromException<MediaWorkerResult>(new IOException("unknown encode stop"))
                };
            });
        await using var f = CreateFixture(true, result is "valid" or "verify-cancel" ? null : injected, null);
        var source = f.Original(SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.CodecImage()), "data.bin", MediaReconciliationSourceRole.Bin, 1);
        var second = new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Bin, "audio.bin", source.Graph, source.Output.Id, source.VerificationId);
        var cue = f.Stored(Encoding.UTF8.GetBytes("FILE \"data.bin\" BINARY\n TRACK 01 MODE1/2352\n INDEX 01 00:00:00\nFILE \"audio.bin\" BINARY\n TRACK 02 AUDIO\n INDEX 01 00:00:00\n"), "disc.cue", MediaReconciliationSourceRole.Cue, 2);
        var sidecar = f.Stored([0x53, 0x42, 0x49, 0, 0xfe], "disc.sbi", MediaReconciliationSourceRole.Sidecar, 3);
        var sources = new MediaReconciliationSources([source, second, cue, sidecar], new MediaReconciliationLayout.Original(42, Guid.NewGuid(), 1, 1, "fixture-catalog", "disc.cue"), [], null);
        var attempt = f.Attempt("ps1-cd-set", sources);
        var acquired = await f.Encoding(f.Profile).AcquireAsync(attempt);
        acquired.IsError.ShouldBeFalse();
        var lease = acquired.Value;
        if (result is "stopped-cancel" or "verify-cancel")
        {
            OperationCanceledException? cancelled = null;
            try
            { await lease.EncodeAndVerifyAsync(_ => Task.FromException(checkpointCancellation)); }
            catch (OperationCanceledException error) { cancelled = error; }
            cancelled.ShouldNotBeNull().ShouldBeSameAs(result == "stopped-cancel" ? certifiedCancellation : checkpointCancellation);
            lease.ProcessTreeStopped.ShouldBeTrue();
            (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
            await lease.DisposeAsync();
            using var afterCancellation = await f.Runtime.AcquireAsync();
            return;
        }
        var encoded = await lease.EncodeAndVerifyAsync(_ => Task.CompletedTask);
        if (result == "unknown")
        {
            encoded.FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
            lease.ProcessTreeStopped.ShouldBeFalse();
            (await lease.ReleaseProcessingAsync()).FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
            await lease.DisposeAsync();
            heldPaths.Length.ShouldBe(7);
            heldPaths.ShouldAllBe(path => File.Exists(path + ".quarantine"));
            await Should.ThrowAsync<ResourceCapacityException>(() => f.Runtime.AcquireAsync());
            return;
        }
        lease.ProcessTreeStopped.ShouldBeTrue();
        if (result == "deadline")
            encoded.FirstError.Code.ShouldBe("Rom.Media.CpuDeadline");
        else if (result == "wall-deadline")
            encoded.FirstError.Code.ShouldBe("Rom.Media.WallDeadline");
        else if (result == "malformed")
            encoded.FirstError.Code.ShouldBe("Rom.Media.Malformed");
        else if (result == "unsupported")
            encoded.FirstError.Code.ShouldBe("Rom.Media.Unsupported");
        else
        {
            encoded.IsError.ShouldBeFalse(encoded.IsError ? encoded.FirstError.ToString() : null);
            var output = encoded.Value.ShouldBeOfType<MediaReconciliationEncoded.Cd>();
            output.Artifact.DatHashes.ShouldBeNull();
            byte[] actualArtifact = await File.ReadAllBytesAsync(output.Artifact.Path);
            output.ArtifactMeasurement.Sha256.ShouldBe(Sha256.FromBytes(System.Security.Cryptography.SHA256.HashData(actualArtifact)));
            output.ArtifactMeasurement.Md5.ShouldBe(Md5.FromBytes(System.Security.Cryptography.MD5.HashData(actualArtifact)));
            output.ArtifactMeasurement.Sha1.ShouldBe(Sha1.FromBytes(System.Security.Cryptography.SHA1.HashData(actualArtifact)));
            output.ArtifactMeasurement.Size.ShouldBe(actualArtifact.LongLength);
            output.Measured.Measurement.Outputs.Count.ShouldBe(2);
            output.Measured.Measurement.Outputs.Select(member => member.Identity).Distinct().ShouldHaveSingleItem().ShouldBe(source.Output.Identity);
            output.Measured.Measurement.Outputs.Select(member => member.Selector).Distinct().Count().ShouldBe(2);
            output.StoredOriginals.Keys.ShouldBe(["disc.cue", "disc.sbi"], ignoreOrder: true);
            output.Observation.Recipe.ShouldBe(attempt.Capture.Identity.Recipe);
            output.Observation.Encoder.BuildLabel.ShouldBe(attempt.Capture.EncoderBuild);
            output.Measured.Measurement.Decoder.ShouldBe(attempt.Capture.Decoder);
            File.Exists(output.Artifact.Path).ShouldBeTrue();
            f.Catalog.ReceivedCalls().ShouldBeEmpty();
        }
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        await lease.DisposeAsync();
        using var capacity = await f.Runtime.AcquireAsync();
    }

    [LinuxEncodingTheory]
    [InlineData(false, MediaWorkerFailure.Startup)]
    [InlineData(false, MediaWorkerFailure.Crash)]
    [InlineData(false, MediaWorkerFailure.Protocol)]
    [InlineData(false, MediaWorkerFailure.IoFailure)]
    [InlineData(false, MediaWorkerFailure.OutputLimit)]
    [InlineData(false, MediaWorkerFailure.CpuDeadline)]
    [InlineData(false, MediaWorkerFailure.WallDeadline)]
    [InlineData(true, MediaWorkerFailure.Startup)]
    [InlineData(true, MediaWorkerFailure.Crash)]
    [InlineData(true, MediaWorkerFailure.Protocol)]
    [InlineData(true, MediaWorkerFailure.IoFailure)]
    [InlineData(true, MediaWorkerFailure.OutputLimit)]
    [InlineData(true, MediaWorkerFailure.CpuDeadline)]
    [InlineData(true, MediaWorkerFailure.WallDeadline)]
    public async Task Encode_CdStoppedOperationalFailure_PreservesItsCodeAndReleasesAllStagingAndCapacity(bool verification, MediaWorkerFailure failure)
    {
        var injected = Substitute.For<IMediaWorkerSupervisor>();
        await using var f = CreateFixture(true, injected, null);
        var native = f.Deployment.Assets!.Native;
        var real = new MediaWorkerSupervisor(f.Child.Options, () => new MediaWorkerProcessLifetime(), runtime: f.Runtime, chdCandidate: native);
        string[] heldPaths = [];
        var stopped = new MediaWorkerResult(null, failure, null, ProcessTreeStopped: true);
        injected.EncodeChdCdAsync(Arg.Any<ITempFile>(), Arg.Any<IReadOnlyList<ITempFile>>(), Arg.Any<IWritableTempFile>(), Arg.Any<ChdmanCandidate>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                heldPaths = call.Arg<IReadOnlyList<ITempFile>>().Select(file => file.Path)
                    .Append(call.ArgAt<ITempFile>(0).Path).Append(call.Arg<IWritableTempFile>().Path).ToArray();
                return verification ? real.EncodeChdCdAsync(call.ArgAt<ITempFile>(0), call.Arg<IReadOnlyList<ITempFile>>(),
                    call.Arg<IWritableTempFile>(), call.Arg<ChdmanCandidate>(), call.Arg<CancellationToken>()) : Task.FromResult(stopped);
            });
        injected.VerifyChdCdAsync(Arg.Any<ITempFile>(), Arg.Any<ITempFile>(), Arg.Any<IReadOnlyList<ITempFile>>(), Arg.Any<CancellationToken>())
            .Returns(stopped);
        var bin = f.Original(SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.CodecImage()), "disc.bin", MediaReconciliationSourceRole.Bin, 1);
        var cue = f.Stored(Encoding.UTF8.GetBytes("FILE \"disc.bin\" BINARY\n TRACK 01 MODE1/2352\n INDEX 01 00:00:00\n"), "disc.cue", MediaReconciliationSourceRole.Cue, 2);
        var attempt = f.Attempt("ps1-cd-set", new([bin, cue], new MediaReconciliationLayout.Original(42, Guid.NewGuid(), 1, 1, "fixture-catalog", "disc.cue"), [], null));
        attempt.Capture.Decoder.ShouldBe(new RomDecoderIdentity.Native(ChdBuildEvidence.Decoder(native.ExpectedIdentity)));
        var acquired = await f.Encoding(f.Profile).AcquireAsync(attempt);
        acquired.IsError.ShouldBeFalse();
        var lease = acquired.Value;
        int checkpoints = 0;
        var result = await lease.EncodeAndVerifyAsync(_ => { checkpoints++; return Task.CompletedTask; });
        result.FirstError.ShouldBe(MediaInspectionErrors.Failed(failure));
        checkpoints.ShouldBe(verification ? 1 : 0);
        lease.ProcessTreeStopped.ShouldBeTrue();
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        await lease.DisposeAsync();
        heldPaths.ShouldNotBeEmpty();
        heldPaths.ShouldAllBe(path => !File.Exists(path));
        Directory.GetFiles(f.Root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        using var next = await f.Runtime.AcquireAsync();
        await injected.Received(1).EncodeChdCdAsync(Arg.Any<ITempFile>(), Arg.Any<IReadOnlyList<ITempFile>>(), Arg.Any<IWritableTempFile>(), Arg.Any<ChdmanCandidate>(), Arg.Any<CancellationToken>());
        if (verification)
            await injected.Received(1).VerifyChdCdAsync(Arg.Any<ITempFile>(), Arg.Any<ITempFile>(), Arg.Any<IReadOnlyList<ITempFile>>(), Arg.Any<CancellationToken>());
        else
            await injected.DidNotReceiveWithAnyArgs().VerifyChdCdAsync(default!, default!, default!, default);
    }

    [LinuxEncodingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encode_DvdCodec_IndependentlyMeasuresRawInputAndSupervisedStoredChdExtraction(bool storedSource)
    {
        await using var f = CreateFixture(true, null, null);
        var source = f.Original(SyntheticPs2Iso.CodecImage(), "disc.iso", MediaReconciliationSourceRole.Whole, 1);
        var acquired = await f.Encoding(f.Profile).AcquireAsync(f.Attempt("ps2-dvd", new([source], null, [], null)));
        acquired.IsError.ShouldBeFalse();
        var lease = acquired.Value;
        var result = await lease.EncodeAndVerifyAsync(_ => Task.CompletedTask);
        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.ToString() : null);
        var output = result.Value.ShouldBeOfType<MediaReconciliationEncoded.Whole>();
        output.Measured.Measurement.Outputs.Single().Identity.ShouldBe(source.Output.Identity);
        output.SourceMeasurement.Representation.MatchesRetained(source.Graph).ShouldBeTrue();
        output.SourceMeasurement.Representation.Measurement.Decoder.ShouldBe(f.Profile.OriginalInspectionDecoder);
        output.SourceMeasurement.Inspection.Identity.ShouldBe(source.Output.Identity);
        output.Measurement.Inspection.Layout.ShouldBeOfType<RomPs2BootLayoutFacts>();
        output.NativeEncoder.ShouldNotBeNull();
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        if (storedSource)
        {
            // Direct codec qualification deliberately exercises another invocation over measured CHD bytes.
            // It grants no planner, reference, ownership or publication authority.
            var captured = await f.RetainAsync(output, "disc.chd", 2, source.Output.RomId!.Value);
            var next = await f.Encoding(f.Profile).AcquireAsync(f.Attempt("ps2-dvd", new([captured], null, [], null)));
            next.IsError.ShouldBeFalse();
            var nextLease = next.Value;
            var repeated = await nextLease.EncodeAndVerifyAsync(_ => Task.CompletedTask);
            repeated.IsError.ShouldBeFalse(repeated.IsError ? repeated.FirstError.ToString() : null);
            var whole = repeated.Value.ShouldBeOfType<MediaReconciliationEncoded.Whole>();
            whole.SourceMeasurement.Representation.MatchesRetained(captured.Graph).ShouldBeTrue();
            whole.SourceMeasurement.Representation.Measurement.Decoder.ShouldBe(whole.Measured.Measurement.Decoder);
            whole.SourceMeasurement.Inspection.Layout.ShouldBeOfType<RomPs2BootLayoutFacts>();
            repeated.Value.Measured.Measurement.Outputs.Single().Identity.ShouldBe(source.Output.Identity);
            repeated.Value.Artifact.FileSha256.ShouldBe(output.Artifact.FileSha256);
            repeated.Value.ComparedAt.ShouldBeGreaterThanOrEqualTo(output.ComparedAt);
            (await nextLease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
            await nextLease.DisposeAsync();
        }
        await lease.DisposeAsync();
        using var capacity = await f.Runtime.AcquireAsync();
    }

    [LinuxEncodingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encode_ActualEncoderReceiptContradictsCapturedBuild_RejectsMeasurementWithoutRequestingAnotherIdenticalInvocation(bool cd)
    {
        await using var f = CreateFixture(cd, null, null);
        string kind = cd ? "ps1-cd-set" : "gamecube-disc";
        var input = f.Original(cd ? SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.CodecImage()) : SyntheticNintendoIso.GameCube(),
            cd ? "data.bin" : "disc.iso", cd ? MediaReconciliationSourceRole.Bin : MediaReconciliationSourceRole.Whole, 1);
        var inputs = new List<MediaReconciliationInput> { input };
        if (cd)
            inputs.Add(f.Stored(Encoding.UTF8.GetBytes("FILE \"data.bin\" BINARY\n TRACK 01 MODE1/2352\n INDEX 01 00:00:00\n"), "disc.cue", MediaReconciliationSourceRole.Cue, 2));
        var sources = new MediaReconciliationSources(inputs, cd ? new MediaReconciliationLayout.Original(42, Guid.NewGuid(), 1, 1, "fixture-catalog", "disc.cue") : null, [], null);
        var expected = f.Attempt(kind, sources).Capture;
        var advertised = Substitute.For<IMediaReconciliationMediaProfile>();
        advertised.LimitsFingerprint.Returns(f.Profile.LimitsFingerprint);
        advertised.OriginalInspectionDecoder.Returns(f.Profile.OriginalInspectionDecoder);
        advertised.Encoder(kind).Returns(new MediaReconciliationEncoder("inconsistent-build-advertisement", expected.DiscKind));
        var capture = new MediaReconciliationCapture(expected.JobId, expected.RequestedVersion, sources, MediaReconciliationOperation.Encode, expected.Identity.Recipe,
            Hash('c'), expected.Identity.LimitsFingerprint, expected.Decoder, kind, "inconsistent-build-advertisement", expected.DiscKind);
        var acquired = await f.Encoding(advertised).AcquireAsync(new(1, capture, null));
        acquired.IsError.ShouldBeFalse();
        await using var lease = acquired.Value;
        var result = await lease.EncodeAndVerifyAsync(_ => Task.CompletedTask);
        result.FirstError.ShouldBe(MediaReconciliationErrors.Measurement);
        lease.ProcessTreeStopped.ShouldBeTrue();
        (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Profile_ChangedStagingLimits_InvalidatesUnchangedFailureIdentityWithoutChangingRegisteredRecipes()
    {
        await using var f = CreateFixture(false, null, null);
        var before = f.Profile.LimitsFingerprint;
        var changed = new TempStorageOptions { MaximumFiles = f.Temporary.MaximumFiles + 1, MaximumBytes = f.Temporary.MaximumBytes, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 };
        var after = new MediaReconciliationMediaProfile(f.Child.Options, changed, f.Application, f.Deployment, f.Composition, f.Capabilities);
        before.ShouldNotBe(after.LimitsFingerprint);
        after.Encoder("gamecube-disc").ShouldBe(f.Profile.Encoder("gamecube-disc"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_ChangedChdCpuOrWallBudget_InvalidatesFailureIdentityWithoutChangingEncoderRecipe(bool cpu)
    {
        await using var f = CreateFixture(false, null, null);
        var options = f.Child.Options.ValidatedCopy();
        if (cpu)
            options.ChdEncodeCpuTimeoutSeconds++;
        else
            options.ChdEncodeWallTimeoutSeconds++;
        var changed = new MediaReconciliationMediaProfile(options, f.Temporary, f.Application, f.Deployment, f.Composition, f.Capabilities);
        changed.LimitsFingerprint.ShouldNotBe(f.Profile.LimitsFingerprint);
        changed.Encoder("gamecube-disc").ShouldBe(f.Profile.Encoder("gamecube-disc"));
        changed.Encoder("ps1-cd-set").ShouldBe(f.Profile.Encoder("ps1-cd-set"));
    }

    private static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    private sealed class Bytes(byte[] bytes) : IRandomAccessContent, ISynchronousRawContent
    {
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
            if (count > 0)
                bytes.AsMemory(checked((int)offset), count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }
        public int ReadAt(long offset, Span<byte> destination)
        {
            int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
            if (count > 0)
                bytes.AsSpan(checked((int)offset), count).CopyTo(destination);
            return count;
        }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private Fixture CreateFixture(bool useNative, IMediaWorkerSupervisor? supervisorOverride, IRomOriginalMediaIdentifier? identifierOverride)
        => new(useNative, supervisorOverride, identifierOverride, testOutput.WriteLine);

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("romd-reconciliation-codec-").FullName;
        public MediaWorkerTestFixture Child { get; } = new();
        public IRomdOptions Application { get; } = Substitute.For<IRomdOptions>();
        public TempStorageOptions Temporary { get; } = new() { MaximumFiles = 32, MaximumBytes = 256L << 20, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 };
        public RomMediaComposition Composition { get; } = RomMediaPolicyComposition.Create();
        public IMediaHostCapabilities Capabilities { get; } = Substitute.For<IMediaHostCapabilities>();
        public IFileStorageService Files { get; } = Substitute.For<IFileStorageService>();
        public IRomContentCatalog Catalog { get; } = Substitute.For<IRomContentCatalog>();
        public Dictionary<int, byte[]> Payloads { get; } = [];
        private readonly Dictionary<StorageKey, byte[]> _objects = [];
        private readonly Dictionary<int, RomRepresentationFacts> _graphs = [];
        private readonly LocalTempFileFactory _staging;
        private readonly IMediaWorkerSupervisor _supervisor;
        private readonly RvzVerifier _rvz;
        private readonly IRomOriginalMediaIdentifier _originals;
        private readonly RomRepresentationDecoder _decoder;
        private readonly ChdMediaAdapter? _nativeReader;
        private readonly RomContentReader _reader;
        public MediaProcessingRuntime Runtime { get; }
        public ChdDeploymentRuntime Deployment { get; }
        public MediaReconciliationMediaProfile Profile { get; }
        public Fixture(bool useNative, IMediaWorkerSupervisor? supervisorOverride, IRomOriginalMediaIdentifier? identifierOverride, Action<string> write)
        {
            Application.DataDirectory.Returns(Root);
            Application.MaxUploadBytes.Returns(64L << 20);
            Child.Options.MaxArtifactBytes = 64L << 20;
            Child.Options.MaxLogicalBytes = 64L << 20;
            Child.Options.MaxConcurrent = 1;
            Child.Options.AdmissionTimeoutSeconds = 1;
            Runtime = new(Application, Child.Options);
            _staging = new(Application, new FileSystem(), Temporary);
            var nativeOptions = useNative ? new NativeToolOptions { LibchdrDirectory = NativeTestBuilds.LibchdrDirectory, ChdmanDirectory = NativeTestBuilds.ChdmanDirectory } : new NativeToolOptions();
            Deployment = new(nativeOptions, true);
            if (useNative)
                Deployment.Verify();
            Capabilities.CanEncode(Arg.Any<string>()).Returns(true);
            Capabilities.CanDecode(Arg.Any<string>()).Returns(true);
            Capabilities.ManagedDecoderBuild.Returns(MediaInspectionService.DecoderBuild);
            Capabilities.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            if (Deployment.Assets is { } assets)
                Capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(ChdBuildEvidence.Decoder(assets.Native.ExpectedIdentity));
            Profile = new(Child.Options, Temporary, Application, Deployment, Composition, Capabilities);
            _supervisor = new RecordingMediaWorkerSupervisor(supervisorOverride ?? new MediaWorkerSupervisor(Child.Options, () => new MediaWorkerProcessLifetime(), runtime: Runtime, chdCandidate: Deployment.Assets?.Native), write);
            _rvz = new(Child.Options, _supervisor, Runtime);
            _originals = identifierOverride ?? new RomOriginalMediaIdentifier(_rvz);
            Files.RetrieveByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => Payloads.TryGetValue(call.Arg<int>(), out var bytes) ? new MemoryStream(bytes) : null);
            var cas = Substitute.For<IContentAddressableStore>();
            cas.OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>()).Returns(call => _objects.TryGetValue(call.Arg<StorageKey>(), out var bytes) ? new Bytes(bytes) : null);
            cas.OpenSynchronousRaw(Arg.Any<StorageKey>()).Returns(call => _objects.TryGetValue(call.Arg<StorageKey>(), out var bytes) ? new Bytes(bytes) : null);
            if (Deployment.Assets is { } ready)
            {
                var limits = new ReadDecoderOptions { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 1 << 20 };
                var cache = new MediaBlockCache(limits.CacheBytes);
                _nativeReader = new(ready.Native, cache, limits);
                _decoder = new(new RomMediaAdapterRegistry(_nativeReader), limits, cache);
                var admission = Substitute.For<IRomChdDeliveryAdmission>();
                var build = ChdBuildEvidence.Decoder(ready.Native.ExpectedIdentity);
                admission.AdmitsCd(Arg.Any<RomReconstruction.CdMember>()).Returns(call => call.Arg<RomReconstruction.CdMember>().HasDecoder(build));
                admission.AssessCd(Arg.Any<RomReconstruction.CdMember>()).Returns(call => call.Arg<RomReconstruction.CdMember>().HasDecoder(build) ? build : null);
                _reader = new(Catalog, cas, _decoder, admission);
            }
            else
            { _decoder = new(); _reader = new(Catalog, cas, _decoder); }
        }
        public MediaReconciliationInput.Logical Original(byte[] bytes, string path, MediaReconciliationSourceRole role, int id)
        {
            var identity = MediaFactFixtures.Identity(bytes);
            Payloads.Add(id, bytes);
            _objects.TryAdd(StorageKey.FromHash(identity.Sha256), bytes);
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity, RomContainerFacts.Identity.Instance,
                new RomDecoderIdentity.Managed("retained-hasher"), "identity/v1", "fixture", DateTimeOffset.UnixEpoch, null, null);
            var graph = new RomRepresentationFacts(id, id, identity.Sha256, identity.Size, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance,
                [new(100 + id, id, RomOutputSelector.Whole.Instance, identity)], [proof], []);
            _graphs.Add(id, graph);
            return new(role, path, graph, 100 + id, proof.Id);
        }
        public async Task<MediaReconciliationInput.Logical> RetainAsync(MediaReconciliationEncoded.Whole encoded, string path, int file, int romId)
        {
            byte[] bytes = await File.ReadAllBytesAsync(encoded.Artifact.Path);
            Payloads.Add(file, bytes);
            var measured = encoded.Measured;
            var identity = measured.Measurement.Outputs.Single().Identity;
            var graph = new RomRepresentationFacts(file, file, encoded.Artifact.FileSha256, encoded.Artifact.FileSize, measured.Container, measured.Manifest,
                [new(100 + file, romId, RomOutputSelector.Whole.Instance, identity)], [measured.Measurement], []);
            _graphs.Add(file, graph);
            return new(MediaReconciliationSourceRole.Whole, path, graph, 100 + file, measured.Measurement.Id);
        }
        public MediaReconciliationInput.Stored Stored(byte[] bytes, string path, MediaReconciliationSourceRole role, int file)
        {
            var identity = MediaFactFixtures.Identity(bytes);
            Payloads.Add(file, bytes);
            return new(role, path, file, new(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32));
        }
        public MediaReconciliationAttempt Attempt(string kind, MediaReconciliationSources sources)
        {
            var policy = Composition.Reconciliation[kind];
            var encoder = Profile.Encoder(kind).ShouldNotBeNull();
            RomDecoderIdentity decoder = policy.Kind.StoredEncoding == RomRepresentationEncoding.Chd ? new RomDecoderIdentity.Native(Capabilities.Decoder(RomRepresentationEncoding.Chd)!) : new RomDecoderIdentity.Managed(Capabilities.ManagedDecoderBuild);
            return new(1, new(Guid.NewGuid(), 1, sources, MediaReconciliationOperation.Encode, policy.Recipe, Hash('c'), Profile.LimitsFingerprint, decoder, kind, encoder.Build, encoder.WholeDiscKind), null);
        }
        public MediaReconciliationInput.Logical CdSource(RomRepresentationFacts graph, byte[] bytes, string path)
        {
            _objects.Add(StorageKey.FromHash(graph.ArtifactSha256), bytes);
            _graphs.Add(graph.Id, graph);
            var output = graph.Outputs.Single();
            return new(MediaReconciliationSourceRole.Bin, path, graph, output.Id, graph.Verifications.Single().Id);
        }
        public RomCdReconciliationAdoption Adoption(IMediaReconciliationMediaProfile executionProfile, IMediaWorkerSupervisor? supervisor)
            => new(Composition, Capabilities, executionProfile, _staging, Files, _reader, supervisor ?? _supervisor, Runtime, TimeProvider.System);
        public MediaReconciliationEncoding Encoding(IMediaReconciliationMediaProfile executionProfile)
        {
            var facts = Substitute.For<IMediaFactReader>();
            facts.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(_graphs);
            var whole = new RomWholeReconciliationEncoding(_staging, Files, Runtime, Child.Options, _originals, () => _rvz,
                () => new ChdVerifier(Child.Options, _supervisor, Runtime, Deployment.Assets!.Native),
                () => new ChdArtifactEncoder(_supervisor, Child.Options, Deployment.Assets!.Tool!), executionProfile, TimeProvider.System);
            var cd = new RomCdReconciliationEncoding(_staging, Files, _reader, _supervisor, Runtime, Deployment, TimeProvider.System);
            return new(Composition, Capabilities, executionProfile, facts, whole, cd);
        }
        public async ValueTask DisposeAsync()
        { _rvz.Dispose(); await _decoder.DisposeAsync(); if (_nativeReader is not null) await _nativeReader.DisposeAsync(); Child.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
