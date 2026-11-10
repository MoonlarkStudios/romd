using System.IO.Abstractions;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Fake supervised replies establish boundary behavior, never actual CHD/native qualification.</summary>
public sealed class ChdVerifierTests
{
    [Fact]
    public async Task VerifyAsync_StoppedCompleteReply_BindsFullMeasurementIntegrityAndFrozenDecoderTuple()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        var before = DateTimeOffset.UtcNow;

        var result = await verifier.VerifyAsync(source);

        result.IsError.ShouldBeFalse();
        var proof = result.Value.Verification;
        var integrity = result.Value.Integrity;
        proof.Id.ShouldNotBe(Guid.Empty);
        proof.ArtifactSha256.ShouldBe(source.FileSha256);
        proof.ArtifactSize.ShouldBe(source.FileSize);
        proof.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        proof.ContentSha256.ShouldBe(Sha256.Parse(new string('a', 64)));
        proof.ContentSize.ShouldBe(64 * 2048);
        proof.Sha1.ShouldBe(Sha1.Parse(new string('b', 40)));
        proof.Md5.ShouldBe(Md5.Parse(new string('c', 32)));
        proof.Crc32.ShouldBe(Crc32.Parse("12345678"));
        proof.Profile.ShouldBe(ChdVerifier.Profile);
        proof.VerifierBuild.ShouldBe(supervisor.Reply!.Identity.WorkerBuild);
        proof.VerifiedAt.Offset.ShouldBe(TimeSpan.Zero);
        proof.VerifiedAt.ShouldBeGreaterThanOrEqualTo(before);
        proof.VerifiedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        integrity.Id.ShouldNotBe(Guid.Empty);
        integrity.VerificationId.ShouldBe(proof.Id);
        integrity.MatchesMeasurement(proof).ShouldBeTrue();
        integrity.MeasurementVerifiedAt.ShouldBe(proof.VerifiedAt);
        integrity.ObservedAt.ShouldBe(proof.VerifiedAt);
        integrity.Profile.ShouldBe(RomChdIntegrity.ProfileVersion);
        integrity.StoredRawSha1.ShouldBe(proof.Sha1);
        integrity.ComputedRawSha1.ShouldBe(proof.Sha1);
        integrity.StoredOverallSha1.ShouldBe(Sha1.Parse(new string('d', 40)));
        integrity.ComputedOverallSha1.ShouldBe(integrity.StoredOverallSha1);
        var expected = fixture.Native.ExpectedIdentity;
        var decoder = integrity.Decoder;
        decoder.Rid.ShouldBe(expected.Rid);
        decoder.UpstreamCommit.ToString().ShouldBe(expected.UpstreamCommit);
        decoder.RecipeVersion.ShouldBe(expected.RecipeVersion);
        decoder.BuildLabel.ShouldBe("libchdr/linux-x64/pin:" + expected.UpstreamCommit + "/recipe:1");
        proof.DecoderBuild.ShouldBe(decoder.BuildLabel);
        result.Value.Ps2Boot.ShouldBeNull();
        result.Value.Inspection.Identity.ShouldBe(proof.Identity);
        result.Value.Inspection.Prefix.ToArray().ShouldBe(new byte[32]);
        result.Value.Container.Codecs.ShouldBe(new uint[] { 0x7a6c6962, 0x6c7a6d61, 0, 0 });
        result.Value.Container.Metadata.ShouldHaveSingleItem().Payload.ToArray().ShouldBe(new byte[] { 0 });
        result.Value.Container.Fingerprint.ShouldNotBeNull();
        supervisor.Calls.ShouldBe(1);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task VerifyAsync_ObservedParametersChange_CapturesActualGeometryCodecSlotsAndMetadataWithoutCanonicalClaim()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        var first = (await verifier.VerifyAsync(source)).Value;
        var inspection = supervisor.Reply!.ChdInspection!;
        supervisor.Reply = supervisor.Reply! with
        {
            ChdInspection = inspection with
            {
                Header = inspection.Header with
                { HunkBytes = 8192, HunkCount = 16, Codec0 = 0x7a737464, Codec1 = 0x7a6c6962, Codec2 = 0x68756666, Codec3 = 0x666c6163 }
            }
        };

        var second = (await verifier.VerifyAsync(source)).Value;

        first.Verification.CapturedEncodingProfile.ShouldNotBe(second.Verification.CapturedEncodingProfile);
        second.Verification.CapturedEncodingProfile.ShouldBe("chd-observed/v1;version=5;logical=131072;hunk=8192;unit=2048;count=16;codecs=7a737464,7a6c6962,68756666,666c6163;parent=none;metadata=chd-dvd-metadata/v1:1:44564420:1:1:0:c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406");
        second.Integrity.Parameters.HunkBytes.ShouldBe(8192U);
        second.Integrity.Parameters.HunkCount.ShouldBe(16U);
        second.Integrity.Parameters.UnitBytes.ShouldBe(2048U);
        second.Integrity.Parameters.Version.ShouldBe(5U);
        second.Integrity.Parameters.HasParent.ShouldBeFalse();
        second.Integrity.Parameters.Metadata.Fingerprint.ShouldBe(RomChdDvdMetadataFacts.SingletonFingerprint);
        second.Integrity.Parameters.CapturedEncodingProfile.ShouldBe(second.Verification.CapturedEncodingProfile);
        second.Integrity.MatchesMeasurement(second.Verification).ShouldBeTrue();
        second.Verification.CapturedEncodingProfile!.ShouldNotContain("canonical");
    }

    [Theory]
    [InlineData("missing-measurement")]
    [InlineData("missing-chd")]
    [InlineData("missing-native")]
    [InlineData("missing-metadata")]
    [InlineData("artifact-hash")]
    [InlineData("artifact-size")]
    [InlineData("header-artifact-size")]
    [InlineData("header-logical-size")]
    [InlineData("logical-sha256")]
    [InlineData("logical-sha1")]
    [InlineData("logical-md5")]
    [InlineData("logical-crc32")]
    [InlineData("logical-prefix-length")]
    [InlineData("logical-prefix-hex")]
    [InlineData("uppercase-hash")]
    [InlineData("raw-mismatch")]
    [InlineData("raw-measurement-mismatch")]
    [InlineData("overall-mismatch")]
    [InlineData("empty-overall")]
    [InlineData("profile")]
    [InlineData("version")]
    [InlineData("unit")]
    [InlineData("hunk-count")]
    [InlineData("cd-codec")]
    [InlineData("metadata-shape")]
    [InlineData("metadata-fingerprint")]
    [InlineData("metadata-hash-case")]
    [InlineData("decoder")]
    [InlineData("worker")]
    [InlineData("status")]
    [InlineData("unexpected-iso")]
    [InlineData("disc-kind")]
    [InlineData("execution")]
    [InlineData("pid")]
    [InlineData("exit-code")]
    [InlineData("heap-limit")]
    [InlineData("stdout-limit")]
    [InlineData("stderr-limit")]
    public async Task VerifyAsync_InconsistentStoppedReply_ReturnsProtocolWithoutEvidenceOrQuarantineAndReleasesCapacity(string defect)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        var reply = supervisor.Reply!;
        var measurement = reply.Inspection!;
        var inspection = reply.ChdInspection!;
        supervisor.Reply = defect switch
        {
            "missing-measurement" => reply with { Inspection = null },
            "missing-chd" => reply with { ChdInspection = null },
            "missing-native" => reply with { NativeIdentity = null },
            "missing-metadata" => reply with { ChdInspection = inspection with { DvdMetadata = null } },
            "artifact-hash" => reply with { Inspection = measurement with { ArtifactSha256 = new string('f', 64) } },
            "artifact-size" => reply with { Inspection = measurement with { ArtifactBytes = source.FileSize + 1 } },
            "header-artifact-size" => reply with { ChdInspection = inspection with { Header = inspection.Header with { ArtifactBytes = source.FileSize + 1 } } },
            "header-logical-size" => reply with { ChdInspection = inspection with { Header = inspection.Header with { LogicalBytes = 2048 } } },
            "logical-sha256" => reply with { Inspection = measurement with { LogicalSha256 = new string('0', 64) } },
            "logical-sha1" => reply with { Inspection = measurement with { Sha1 = "bad" } },
            "logical-md5" => reply with { Inspection = measurement with { Md5 = new string('0', 32) } },
            "logical-crc32" => reply with { Inspection = measurement with { Crc32 = "0000000g" } },
            "logical-prefix-length" => reply with { Inspection = measurement with { LogicalPrefix = "00" } },
            "logical-prefix-hex" => reply with { Inspection = measurement with { LogicalPrefix = new string('g', 64) } },
            "uppercase-hash" => reply with { Inspection = measurement with { LogicalSha256 = new string('A', 64) } },
            "raw-mismatch" => reply with { ChdInspection = inspection with { StoredRawSha1 = new string('e', 40) } },
            "raw-measurement-mismatch" => reply with { ChdInspection = inspection with { StoredRawSha1 = new string('e', 40), ComputedRawSha1 = new string('e', 40) } },
            "overall-mismatch" => reply with { ChdInspection = inspection with { StoredOverallSha1 = new string('e', 40) } },
            "empty-overall" => reply with { ChdInspection = inspection with { StoredOverallSha1 = new string('0', 40), ComputedOverallSha1 = new string('0', 40) } },
            "profile" => reply with { Inspection = measurement with { EncodingProfile = "chd-dvd-canonical/v1" } },
            "version" => reply with { ChdInspection = inspection with { Version = 4 } },
            "unit" => reply with { ChdInspection = inspection with { Header = inspection.Header with { UnitBytes = 2352 } } },
            "hunk-count" => reply with { ChdInspection = inspection with { Header = inspection.Header with { HunkCount = 1 } } },
            "cd-codec" => reply with { ChdInspection = inspection with { Header = inspection.Header with { Codec0 = 0x63647a6c } } },
            "metadata-shape" => reply with { ChdInspection = inspection with { DvdMetadata = inspection.DvdMetadata! with { EntryCount = 2 } } },
            "metadata-fingerprint" => reply with { ChdInspection = inspection with { DvdMetadata = inspection.DvdMetadata! with { NormalizedSha256 = new string('e', 64) } } },
            "metadata-hash-case" => reply with { ChdInspection = inspection with { DvdMetadata = inspection.DvdMetadata! with { NormalizedSha256 = inspection.DvdMetadata!.NormalizedSha256.ToUpperInvariant() } } },
            "decoder" => reply with { Identity = reply.Identity with { DecoderBuild = "libchdr/unrelated" } },
            "worker" => reply with { Identity = reply.Identity with { WorkerBuild = "romd-media-worker/mvid:00000000-0000-0000-0000-000000000000" } },
            "status" => reply with { Status = MediaWorkerStatus.Ready },
            "unexpected-iso" => reply with { IsoInspection = new(Romd.Media.Nintendo.NintendoIsoInspectionStatus.Unsupported, "nintendo-iso/v1") },
            "disc-kind" => reply with { Inspection = measurement with { DiscKind = Romd.Media.Nintendo.NintendoDiscKind.GameCube } },
            "pid" => reply with { Identity = reply.Identity with { ProcessId = reply.Identity.ProcessId + 1 } },
            "heap-limit" => reply with { Identity = reply.Identity with { GcHeapLimitBytes = fixture.Options.MemoryBytes + 1 } },
            _ => reply
        };
        supervisor.Execution = defect switch
        {
            "execution" => null,
            "exit-code" => supervisor.Execution! with { ExitCode = 1 },
            "stdout-limit" => supervisor.Execution! with { StandardOutputBytes = MediaWorkerProtocol.MaximumOutputBytes + 1 },
            "stderr-limit" => supervisor.Execution! with { StandardErrorBytes = 16385 },
            _ => supervisor.Execution
        };
        using var verifier = fixture.Verifier(supervisor);

        var result = await verifier.VerifyAsync(source);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.Media.Protocol");
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        supervisor.Calls.ShouldBe(1);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("file")]
    [InlineData("binary-bytes")]
    [InlineData("binary-hash")]
    [InlineData("upstream-commit")]
    [InlineData("upstream-version")]
    [InlineData("recipe")]
    [InlineData("family-version")]
    [InlineData("build-id")]
    public async Task VerifyAsync_AnyVerifiedNativeIdentityFieldDiffers_RefusesEvidence(string field)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        var native = fixture.Native.ExpectedIdentity;
        supervisor.Reply = supervisor.Reply! with
        {
            NativeIdentity = field switch
            {
                "rid" => native with { Rid = "win-x64" },
                "file" => native with { File = "libother_chdr.so" },
                "binary-bytes" => native with { BinaryBytes = native.BinaryBytes + 1 },
                "binary-hash" => native with { BinarySha256 = new string('f', 64) },
                "upstream-commit" => native with { UpstreamCommit = new string('f', 40) },
                "upstream-version" => native with { UpstreamVersion = "different-upstream" },
                "recipe" => native with { RecipeVersion = native.RecipeVersion + 1 },
                "family-version" => native with { FamilyVersion = "different-family" },
                "build-id" => native with { BuildId = new string('f', 64) },
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            }
        };
        using var verifier = fixture.Verifier(supervisor);

        (await verifier.VerifyAsync(source)).FirstError.Code.ShouldBe("Rom.Media.Protocol");

        supervisor.Calls.ShouldBe(1);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("logical")]
    public async Task VerifyAsync_ConfiguredLimits_RefusesWithoutEvidenceAndReleasesSlot(string limit)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        if (limit == "artifact")
            fixture.Options.MaxArtifactBytes = source.FileSize - 1;
        else
            fixture.Options.MaxLogicalBytes = 2048;
        using var verifier = fixture.Verifier(supervisor);

        var result = await verifier.VerifyAsync(source);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(limit == "artifact" ? "Rom.Media.LimitExceeded" : "Rom.Media.Protocol");
        supervisor.Calls.ShouldBe(limit == "artifact" ? 0 : 1);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("ffffffff")]
    public async Task VerifyAsync_ValidBoundaryCrc32_BindsExactMeasuredChecksumAndIntegrity(string crc)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.Reply = supervisor.Reply! with { Inspection = supervisor.Reply!.Inspection! with { Crc32 = crc } };
        using var verifier = fixture.Verifier(supervisor);

        var result = await verifier.VerifyAsync(source);

        result.IsError.ShouldBeFalse();
        var measured = Crc32.Parse(crc);
        measured.IsEmpty.ShouldBeFalse();
        result.Value.Verification.Crc32.ShouldBe(measured);
        result.Value.Verification.Crc32.ToString().ShouldBe(crc);
        result.Value.Integrity.Crc32.ShouldBe(measured);
        result.Value.Integrity.VerificationId.ShouldBe(result.Value.Verification.Id);
        result.Value.Integrity.MatchesMeasurement(result.Value.Verification).ShouldBeTrue();
        supervisor.Calls.ShouldBe(1);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task VerifyAsync_LegacySupervisorWithoutChdImplementation_FailsClosedWithoutOtherOperationFallback()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        using var verifier = fixture.Verifier(new LegacySupervisor());

        (await verifier.VerifyAsync(source)).FirstError.Code.ShouldBe("Rom.Media.Unsupported");

        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("identified")]
    [InlineData("not-identified")]
    [InlineData("malformed")]
    [InlineData("limit-exceeded")]
    public async Task VerifyAsync_OptionalPs2Observation_KeepsLogicalBootFactsSeparateFromContainerEvidence(string status)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.Reply = supervisor.Reply! with
        {
            Ps2Inspection = status switch
            {
                "none" => null,
                "identified" => Fixture.Boot(),
                "not-identified" => new(Ps2DvdIdentificationStatus.NotIdentified, RomPs2BootLayoutFacts.ProfileVersion),
                "malformed" => new(Ps2DvdIdentificationStatus.Malformed, RomPs2BootLayoutFacts.ProfileVersion),
                "limit-exceeded" => new(Ps2DvdIdentificationStatus.LimitExceeded, RomPs2BootLayoutFacts.ProfileVersion),
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            }
        };
        using var verifier = fixture.Verifier(supervisor);

        var result = await verifier.VerifyAsync(source);

        result.IsError.ShouldBeFalse();
        result.Value.Integrity.MatchesMeasurement(result.Value.Verification).ShouldBeTrue();
        if (status == "identified")
        {
            var boot = result.Value.Ps2Boot.ShouldNotBeNull();
            boot.LogicalBytes.ShouldBe(result.Value.Verification.ContentSize);
            boot.BootSerial.ShouldBe("SLUS_203.12");
            boot.Profile.ShouldBe(RomPs2BootLayoutFacts.ProfileVersion);
            boot.InspectorBuild.ShouldBe(MediaInspectionService.DecoderBuild);
        }
        else
            result.Value.Ps2Boot.ShouldBeNull();
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("status")]
    [InlineData("missing")]
    [InlineData("foreign-content")]
    [InlineData("invalid-layout")]
    [InlineData("unexpected-facts")]
    public async Task VerifyAsync_InconsistentPs2Observation_RefusesWholeResult(string defect)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        var boot = Fixture.Boot();
        supervisor.Reply = supervisor.Reply! with
        {
            Ps2Inspection = defect switch
            {
                "profile" => boot with { Profile = "unrelated" },
                "status" => boot with { Status = (Ps2DvdIdentificationStatus)99 },
                "missing" => boot with { Facts = null },
                "foreign-content" => boot with { Facts = boot.Facts! with { LogicalBytes = 32 * 2048 } },
                "invalid-layout" => boot with { Facts = boot.Facts! with { BootExecutableBlock = 16 } },
                "unexpected-facts" => boot with { Status = Ps2DvdIdentificationStatus.NotIdentified },
                _ => throw new ArgumentOutOfRangeException(nameof(defect))
            }
        };
        using var verifier = fixture.Verifier(supervisor);

        (await verifier.VerifyAsync(source)).FirstError.Code.ShouldBe("Rom.Media.Protocol");

        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Admission_OneUseAndBorrowedLease_DoesNotReleaseOrReplaceOuterReservation()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        using var outer = await fixture.Runtime.AcquireAsync(Guid.NewGuid(), "convergence");
        using var active = outer.Activate();
        using var admission = await verifier.AcquireAsync(default);

        (await admission.VerifyAsync(source, default)).IsError.ShouldBeFalse();
        admission.Dispose();

        supervisor.SeenLease.ShouldBeSameAs(outer);
        outer.Released.ShouldBeFalse();
        File.Exists(outer.RecordPath).ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => admission.VerifyAsync(source, default));
        supervisor.Calls.ShouldBe(1);
        await Should.ThrowAsync<ResourceCapacityException>(() => fixture.Runtime.AcquireAsync());
        outer.Dispose();
        outer.Released.ShouldBeTrue();
    }

    [Fact]
    public async Task Admission_DisposedWhileRunning_DoesNotFreeSlotUntilStoppedReplyReturns()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.BeforeReply = async ct => { began.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var verifier = fixture.Verifier(supervisor);
        using var admission = await verifier.AcquireAsync(default);
        Task<ErrorOr<ChdVerifiedInspection>> running = admission.VerifyAsync(source, cancellation.Token);
        try
        {
            await began.Task.WaitAsync(deadline.Token);
            admission.Dispose();
            await Should.ThrowAsync<ResourceCapacityException>(() => fixture.Runtime.AcquireAsync(ct: deadline.Token));
            release.TrySetResult();
            (await running.WaitAsync(deadline.Token)).IsError.ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync();
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            { await running.WaitAsync(cleanupDeadline.Token); }
            catch (OperationCanceledException) when (running.IsCanceled && cancellation.IsCancellationRequested) { }
        }

        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Admission_CanceledBeforeUse_DoesNotInvokeOrQuarantineAndReleasesUnstartedSlot()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        using var admission = await verifier.AcquireAsync(default);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var error = await Should.ThrowAsync<OperationCanceledException>(() => admission.VerifyAsync(source, cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        supervisor.Calls.ShouldBe(0);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeFalse();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task VerifyAsync_CertifiedStoppedCancellation_PreservesCertificateAndReleasesSourceAndSlot()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        using var cancellation = new CancellationTokenSource();
        var supervisor = fixture.Supervisor(source);
        var certified = new MediaWorkerStoppedCancellationException(cancellation.Token);
        supervisor.BeforeReply = _ =>
        {
            source.TryBeginChildUse().ShouldBeTrue();
            source.TryEndChildUse().ShouldBeTrue();
            cancellation.Cancel();
            throw certified;
        };
        using var verifier = fixture.Verifier(supervisor);

        OperationCanceledException? error = null;
        try
        {
            await verifier.VerifyAsync(source, cancellation.Token);
        }
        catch (OperationCanceledException caught)
        {
            error = caught;
        }

        error.ShouldNotBeNull().ShouldBeSameAs(certified);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeFalse();
        using var recovered = await fixture.Runtime.AcquireAsync();
        await using var reusable = (await fixture.Files.ReserveOutputAsync(1)).Value;
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unrequested-cancellation")]
    [InlineData("requested-cancellation")]
    [InlineData("unconfirmed-result")]
    [InlineData("quarantine-io")]
    public async Task VerifyAsync_UncertainStop_QuarantinesActualSourceAndRetainsSharedProcessingAndStagingCapacity(string failure)
    {
        using var fixture = new Fixture();
        var actual = await fixture.SourceAsync();
        ITempFile source = failure == "quarantine-io" ? new ThrowingQuarantineInput(actual) : actual;
        using var cancellation = new CancellationTokenSource();
        var supervisor = fixture.Supervisor(source);
        supervisor.BeforeReply = _ =>
        {
            source.TryBeginChildUse().ShouldBeTrue();
            if (failure == "unconfirmed-result")
            { supervisor.Stopped = false; return Task.CompletedTask; }
            if (failure == "requested-cancellation")
                cancellation.Cancel();
            if (failure is "unrequested-cancellation" or "requested-cancellation")
                throw new OperationCanceledException(new CancellationToken(canceled: true));
            throw new IOException("fake supervision lost its stop proof");
        };
        using var verifier = fixture.Verifier(supervisor);
        try
        {
            if (failure == "requested-cancellation")
            {
                var error = await Should.ThrowAsync<OperationCanceledException>(() => verifier.VerifyAsync(source, cancellation.Token));
                error.CancellationToken.ShouldBe(cancellation.Token);
            }
            else
                (await verifier.VerifyAsync(source)).FirstError.Code.ShouldBe("Rom.Media.Containment");
            source.TryEndChildUse().ShouldBeFalse();
        }
        finally { await source.DisposeAsync(); }

        supervisor.Calls.ShouldBe(1);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeFalse();
        File.Exists(source.Path).ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(() => fixture.Runtime.AcquireAsync());
        await Should.ThrowAsync<ResourceCapacityException>(() => fixture.Files.ReserveOutputAsync(1));
    }

    [Fact]
    public async Task VerifyAsync_CanceledAfterStoppedReply_ReturnsCancellationWithoutRetainingSafeCapacity()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        using var cancellation = new CancellationTokenSource();
        var supervisor = fixture.Supervisor(source);
        supervisor.BeforeReply = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        using var verifier = fixture.Verifier(supervisor);

        var error = await Should.ThrowAsync<OperationCanceledException>(() => verifier.VerifyAsync(source, cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task VerifyAsync_UnconfirmedBorrowedRun_RetainsOuterLeaseEvenWhenItsOwnerDisposes()
    {
        using var fixture = new Fixture();
        var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.BeforeReply = _ =>
        {
            source.TryBeginChildUse().ShouldBeTrue();
            supervisor.Stopped = false;
            return Task.CompletedTask;
        };
        using var verifier = fixture.Verifier(supervisor);
        using var outer = await fixture.Runtime.AcquireAsync(Guid.NewGuid(), "convergence");
        using var active = outer.Activate();
        try
        { (await verifier.VerifyAsync(source)).FirstError.Code.ShouldBe("Rom.Media.Containment"); }
        finally { await source.DisposeAsync(); }

        supervisor.SeenLease.ShouldBeSameAs(outer);
        outer.Dispose();
        outer.Released.ShouldBeFalse();
        File.Exists(outer.RecordPath).ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(() => fixture.Runtime.AcquireAsync());
    }

    [Fact]
    public async Task VerifyAsync_StoppedPermanentFailure_PreservesFailureWithoutEvidenceAndReleasesSlot()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.Failure = MediaWorkerFailure.Malformed;
        using var verifier = fixture.Verifier(supervisor);

        (await verifier.VerifyAsync(source)).FirstError.Code.ShouldBe("Rom.Media.Malformed");

        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("romd-chd-verifier-fake-").FullName;
        public MediaProcessingOptions Options { get; } = new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 };
        public MediaProcessingRuntime Runtime { get; }
        public LocalTempFileFactory Files { get; }
        public ChdNativeCandidate Native { get; }
        public Fixture()
        {
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(_root);
            options.MaxUploadBytes.Returns(8L << 20);
            Runtime = new(options, Options);
            Files = new(options, new FileSystem(), new TempStorageOptions
            { MaximumBytes = 16L << 20, MaximumFiles = 1, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
            Native = new(Path.Combine(_root, "absent-libchdr"),
                new("linux-x64", "libmoonlark_chdr.so", 100, new string('1', 64), new string('4', 40), "test-upstream", 1,
                    "test-family", new string('5', 64), true, true, true));
        }
        public Task<ITempFile> SourceAsync() => Files.CreateAsync(new MemoryStream(new byte[256]));
        public ChdVerifier Verifier(IMediaWorkerSupervisor supervisor) => new(Options, supervisor, Runtime, Native);
        public FakeSupervisor Supervisor(ITempFile source)
        {
            var identity = new MediaWorkerIdentity(123, 123, Options.MemoryBytes, MediaInspectionService.DecoderBuild,
                "romd-media-worker/mvid:7300d4b9-e9d1-49d0-b6d3-17a09b6dc2b1", OperatingSystem.IsLinux()
                    ? new(Options.LinuxAddressSpaceBytes, Options.LinuxDataBytes, Options.CpuTimeoutSeconds, 0) : null,
                Options.MemoryBytes, 0);
            var measurement = new MediaInspection(source.FileSha256.ToString(), source.FileSize, new string('a', 64),
                new string('b', 40), new string('c', 32), "12345678", 64 * 2048, new string('0', 64), "chd-import/v1");
            var inspection = new ChdInspection(5, new(source.FileSize, 64 * 2048, 4096, 2048, 32, 0x7a6c6962, 0x6c7a6d61, 0, 0),
                new string('b', 40), new string('b', 40), new string('d', 40), new string('d', 40),
                new(RomChdDvdMetadataFacts.ProfileVersion, 1, 0x44564420, 1, 1, 0, RomChdDvdMetadataFacts.SingletonFingerprint.ToString()));
            return new(new(MediaWorkerStatus.Inspected, identity, measurement, NativeIdentity: Native.ExpectedIdentity, ChdInspection: inspection));
        }
        public static MediaPs2Inspection Boot() => new(Ps2DvdIdentificationStatus.Identified, RomPs2BootLayoutFacts.ProfileVersion,
            new(64 * 2048, 64, 16, 20, 2048, 22, 64, 24, 128,
                "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x1000, 1));
        public void Dispose() => Directory.Delete(_root, true);
    }

    internal sealed class FakeSupervisor(MediaWorkerReply reply) : IMediaWorkerSupervisor
    {
        public MediaWorkerReply? Reply { get; set; } = reply;
        public MediaWorkerExecution? Execution { get; set; } = new(123, 0, 1024, 0, MediaWorkerDiagnosticKind.None);
        public MediaWorkerFailure? Failure { get; set; }
        public bool Stopped { get; set; } = true;
        public Func<CancellationToken, Task>? BeforeReply { get; set; }
        public int Calls { get; private set; }
        public MediaProcessingRuntime.MediaProcessingReservation? SeenLease { get; private set; }
        public bool IsAvailable => true;
        public async Task<MediaWorkerResult> InspectChdAsync(ITempFile source, CancellationToken ct = default)
        {
            Calls++;
            SeenLease = MediaProcessingRuntime.Current;
            if (BeforeReply is { } before)
                await before(ct);
            return new(Reply, Failure, Execution, Stopped);
        }
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => throw new InvalidOperationException("No handshake fallback.");
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No RVZ fallback.");
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No ISO fallback.");
        public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default) => throw new InvalidOperationException("No encode fallback.");
    }

    private sealed class LegacySupervisor : IMediaWorkerSupervisor
    {
        public bool IsAvailable => true;
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => throw new InvalidOperationException("No handshake fallback.");
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No RVZ fallback.");
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No ISO fallback.");
        public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default) => throw new InvalidOperationException("No encode fallback.");
    }
}
