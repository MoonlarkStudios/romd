using System.Buffers.Binary;
using System.IO.Abstractions;
using System.Text;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Adapter boundaries through actual verifier/reservation code, instrumented replies and existing admitted-child fixtures.
/// Injected child replies and synthetic native descriptors do not qualify real CHD/native decoding.</summary>
public sealed class ChdPreparationAdapterTests
{
    private static readonly byte[] Magic = "MComprHD"u8.ToArray();
    private const string CapturedProfile = "chd-observed/v1;version=5;logical=131072;hunk=4096;unit=2048;count=32;codecs=7a6c6962,6c7a6d61,00000000,00000000;parent=none;metadata=chd-dvd-metadata/v1:1:44564420:1:1:0:c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406";

    [Theory]
    [InlineData("MComprHD", "disc.bin", true)]
    [InlineData("MComprHDmore", "disc.iso", true)]
    [InlineData("MComprH", "disc.bin", false)]
    [InlineData("MComprHX", "disc.bin", false)]
    [InlineData("mComprHD", "disc.bin", false)]
    [InlineData("", "disc.CHD", true)]
    [InlineData("malformed", "disc.chd", true)]
    [InlineData("", "disc.chd.tmp", false)]
    [InlineData("RVZ", "disc.rvz", false)]
    public void Claims_FullMagicOrFilenameHint_DoesNotInspectOrAcquire(string prefix, string filename, bool claimed)
    {
        int factoryCalls = 0;
        var adapter = new ChdPreparationAdapter(() => { factoryCalls++; throw new InvalidOperationException("Detection cannot acquire verification."); }, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        adapter.Claims(Encoding.ASCII.GetBytes(prefix), filename).ShouldBe(claimed);

        adapter.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        adapter.RequiresDatHashes(Magic).ShouldBeFalse();
        factoryCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MComprH")]
    [InlineData("MComprHX")]
    [InlineData("mComprHD")]
    public async Task AdmitAsync_MalformedOrTruncatedMagic_RejectsBeforeAcquiringCapacity(string prefix)
    {
        int factoryCalls = 0;
        var adapter = new ChdPreparationAdapter(() => { factoryCalls++; throw new InvalidOperationException("Bad magic cannot acquire verification."); }, () => throw new NotSupportedException("This fixture has no raw CD inspection."));
        var bytes = Encoding.ASCII.GetBytes(prefix);
        adapter.Claims(bytes, "hint.chd").ShouldBeTrue("the extension selects a rejecting adapter rather than proving valid media");

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            using var unexpected = await adapter.AdmitAsync(bytes, default);
        });

        factoryCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Prepare_StoppedExactInspection_CarriesBorrowedMeasurementIntegrityAndSeparateBoot(bool admitted, bool ps2, bool zeroCrc)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.Reply = supervisor.Reply with
        {
            Inspection = supervisor.Reply.Inspection! with { Crc32 = zeroCrc ? "00000000" : "12345678" },
            Ps2Inspection = ps2 ? Boot() : null
        };
        using var verifier = fixture.Verifier(supervisor);
        int factoryCalls = 0;
        var adapter = new ChdPreparationAdapter(() => { factoryCalls++; return verifier; }, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        var result = await PrepareAsync(adapter, source, admitted);

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Artifact.ShouldBeSameAs(source);
        prepared.LogicalFilename.ShouldBe("Disc.Part1.iso");
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        prepared.ContentSha256.ShouldBe(Sha256.Parse(new string('a', 64)));
        prepared.Verification.ArtifactSha256.ShouldBe(source.FileSha256);
        prepared.Verification.ArtifactSize.ShouldBe(source.FileSize);
        prepared.Verification.Profile.ShouldBe(ChdVerifier.Profile);
        prepared.Verification.VerifierBuild.ShouldBe(supervisor.Reply.Identity.WorkerBuild);
        prepared.Hashes.ShouldBe(new Romd.Admin.Application.Hashing.DatHashResult(
            Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.Parse(zeroCrc ? "00000000" : "12345678"), 131072));
        prepared.Verification.Sha1.ShouldBe(prepared.Hashes.Sha1);
        prepared.Verification.Md5.ShouldBe(prepared.Hashes.Md5);
        prepared.Verification.Crc32.ShouldBe(prepared.Hashes.Crc32);
        prepared.Verification.Crc32.IsEmpty.ShouldBeFalse();
        prepared.EncodingProfile.ShouldBe(CapturedProfile);
        prepared.Verification.CapturedEncodingProfile.ShouldBe(prepared.EncodingProfile);
        var integrity = prepared.ChdIntegrity.ShouldNotBeNull();
        integrity.VerificationId.ShouldBe(prepared.Verification.Id);
        integrity.MatchesMeasurement(prepared.Verification).ShouldBeTrue();
        integrity.MeasurementVerifiedAt.ShouldBe(prepared.Verification.VerifiedAt);
        integrity.Parameters.CapturedEncodingProfile.ShouldBe(prepared.EncodingProfile);
        integrity.Decoder.UpstreamCommit.ToString().ShouldBe(fixture.Native.ExpectedIdentity.UpstreamCommit);
        integrity.Decoder.RecipeVersion.ShouldBe(fixture.Native.ExpectedIdentity.RecipeVersion);
        integrity.Decoder.Rid.ShouldBe(fixture.Native.ExpectedIdentity.Rid);
        if (ps2)
        {
            var boot = prepared.ChdPs2Boot.ShouldNotBeNull();
            boot.BootSerial.ShouldBe("SLUS_203.12");
            boot.LogicalBytes.ShouldBe(prepared.Verification.ContentSize);
            boot.InspectorBuild.ShouldBe(MediaInspectionService.DecoderBuild);
        }
        else
            prepared.ChdPs2Boot.ShouldBeNull();
        prepared.Ps2Observation.ShouldBeNull();
        prepared.Eligibility.ShouldBeNull();
        prepared.SourceEquivalence.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
        await prepared.DisposeAsync();
        source.DisposeCalls.ShouldBe(0);
        File.Exists(source.Path).ShouldBeTrue();
        source.OpenReads.ShouldBe(admitted ? 0 : 1);
        source.ReadBytes.ShouldBe(admitted ? 0 : 124);
        source.QuarantineCalls.ShouldBe(0);
        factoryCalls.ShouldBe(1);
        supervisor.Calls.ShouldBe(1);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
        File.Exists(fixture.Native.BinaryPath).ShouldBeFalse("the host must not attempt native loading");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_ObservedHeaderTuple_CarriesActualProfileWithoutCanonicalRelabeling(bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        var inspection = supervisor.Reply.ChdInspection!;
        supervisor.Reply = supervisor.Reply with
        {
            ChdInspection = inspection with
            {
                Header = inspection.Header with
                { HunkBytes = 8192, HunkCount = 16, Codec0 = 0x7a737464, Codec1 = 0x7a6c6962, Codec2 = 0x68756666, Codec3 = 0x666c6163 }
            }
        };
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        var result = await PrepareAsync(adapter, source, admitted);

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.EncodingProfile.ShouldBe("chd-observed/v1;version=5;logical=131072;hunk=8192;unit=2048;count=16;codecs=7a737464,7a6c6962,68756666,666c6163;parent=none;metadata=chd-dvd-metadata/v1:1:44564420:1:1:0:c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406");
        prepared.Verification.CapturedEncodingProfile.ShouldBe(prepared.EncodingProfile);
        var integrity = prepared.ChdIntegrity.ShouldNotBeNull();
        integrity.Parameters.HunkBytes.ShouldBe(8192U);
        integrity.Parameters.HunkCount.ShouldBe(16U);
        integrity.Parameters.Codec0.ShouldBe(0x7a737464U);
        integrity.Parameters.Codec3.ShouldBe(0x666c6163U);
        prepared.EncodingProfile.ShouldNotContain("canonical");
        prepared.ChdEncodingEvidence.ShouldBeNull();
        source.OpenReads.ShouldBe(admitted ? 0 : 1);
        source.ReadBytes.ShouldBe(admitted ? 0 : 124);
    }

    [Theory]
    [InlineData("artifact", false)]
    [InlineData("profile", true)]
    [InlineData("raw-integrity", false)]
    [InlineData("overall-integrity", true)]
    [InlineData("quartet", false)]
    [InlineData("missing-chd", true)]
    public async Task Prepare_InconsistentStoppedReply_PropagatesProtocolFailureWithoutMetadataFallback(string defect, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        var reply = supervisor.Reply;
        supervisor.Reply = defect switch
        {
            "artifact" => reply with { Inspection = reply.Inspection! with { ArtifactSha256 = new string('f', 64) } },
            "profile" => reply with { Inspection = reply.Inspection! with { EncodingProfile = "chd-dvd-canonical/v1" } },
            "raw-integrity" => reply with { ChdInspection = reply.ChdInspection! with { StoredRawSha1 = new string('e', 40) } },
            "overall-integrity" => reply with { ChdInspection = reply.ChdInspection! with { ComputedOverallSha1 = new string('e', 40) } },
            "quartet" => reply with { Inspection = reply.Inspection! with { Md5 = new string('0', 32) } },
            "missing-chd" => reply with { ChdInspection = null },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        var result = await PrepareAsync(adapter, source, admitted);

        result.Errors.ShouldBe(new[] { MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol) });
        source.DisposeCalls.ShouldBe(0);
        source.OpenReads.ShouldBe(admitted ? 0 : 1);
        source.ReadBytes.ShouldBe(admitted ? 0 : 124);
        source.QuarantineCalls.ShouldBe(0);
        supervisor.Calls.ShouldBe(1);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData(MediaWorkerFailure.Startup, false)]
    [InlineData(MediaWorkerFailure.Startup, true)]
    [InlineData(MediaWorkerFailure.IoFailure, false)]
    [InlineData(MediaWorkerFailure.IoFailure, true)]
    public async Task Prepare_StoppedSupervisorFailure_PreservesExactErrorAndCallerArtifactOwnership(MediaWorkerFailure failure, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.Failure = failure;
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        var result = await PrepareAsync(adapter, source, admitted);

        result.Errors.ShouldBe(new[] { MediaInspectionErrors.Failed(failure) });
        source.DisposeCalls.ShouldBe(0);
        source.OpenReads.ShouldBe(admitted ? 0 : 1);
        source.ReadBytes.ShouldBe(admitted ? 0 : 124);
        supervisor.Calls.ShouldBe(1);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Admission_BorrowedOuterLeaseAndOneUse_DoesNotReacquireReleaseOrInvokeAgain()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        int factoryCalls = 0;
        var adapter = new ChdPreparationAdapter(() => { factoryCalls++; return verifier; }, () => throw new NotSupportedException("This fixture has no raw CD inspection."));
        using var outer = await fixture.Runtime.AcquireAsync(Guid.NewGuid(), "ingestion");
        using var active = outer.Activate();
        using var admission = (await adapter.AdmitAsync(Header(), default)).ShouldNotBeNull();

        var result = await admission.PrepareAsync(source, "borrowed.chd");
        admission.Dispose();

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        supervisor.SeenLease.ShouldBeSameAs(outer);
        outer.Released.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => admission.PrepareAsync(source, "borrowed.chd"));
        factoryCalls.ShouldBe(1);
        supervisor.Calls.ShouldBe(1);
        await AssertOccupiedAsync(fixture.Runtime);
        outer.Dispose();
        outer.Released.ShouldBeTrue();
        source.DisposeCalls.ShouldBe(0);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Admission_DisposedBeforeUse_ReleasesOnlyOwnedSlotAndCannotPrepare()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));
        var admission = (await adapter.AdmitAsync(Header(), default)).ShouldNotBeNull();

        admission.Dispose();

        await Should.ThrowAsync<InvalidOperationException>(() => admission.PrepareAsync(source, "disposed.chd"));
        supervisor.Calls.ShouldBe(0);
        source.DisposeCalls.ShouldBe(0);
        using var second = (await adapter.AdmitAsync(Header(), default)).ShouldNotBeNull();
        second.Dispose(); // Disposing an admission must not dispose the shared verifier.
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Admission_DisposedWhileInspectionRuns_HoldsCapacityUntilStoppedResult()
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
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));
        using var admission = (await adapter.AdmitAsync(Header(), default)).ShouldNotBeNull();
        var running = admission.PrepareAsync(source, "running.chd", cancellation.Token);
        try
        {
            await began.Task.WaitAsync(deadline.Token);
            admission.Dispose();
            await AssertOccupiedAsync(fixture.Runtime, deadline.Token);
            release.TrySetResult();
            var result = await running.WaitAsync(deadline.Token);
            result.IsError.ShouldBeFalse();
            await result.Value.DisposeAsync();
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
        source.DisposeCalls.ShouldBe(0);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("direct")]
    [InlineData("admitted-use")]
    public async Task Prepare_PreCancelled_PropagatesExactCallerTokenWithoutInspectionOrSourceDisposal(string route)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        int factoryCalls = 0;
        var adapter = new ChdPreparationAdapter(() => { factoryCalls++; return verifier; }, () => throw new NotSupportedException("This fixture has no raw CD inspection."));
        using var admission = route == "admitted-use" ? (await adapter.AdmitAsync(Header(), default)).ShouldNotBeNull() : null;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            if (route == "admission")
            {
                using var unexpected = await adapter.AdmitAsync(Header(), cancellation.Token);
            }
            else if (admission is not null)
                await admission.PrepareAsync(source, "cancel.chd", cancellation.Token);
            else
                await adapter.VerifyAsync(source, "cancel.chd", cancellation.Token);
        });

        error.CancellationToken.ShouldBe(cancellation.Token);
        supervisor.Calls.ShouldBe(0);
        factoryCalls.ShouldBe(route == "admitted-use" ? 1 : 0);
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_CertifiedStoppedCancellation_PreservesCertificateAndSafeOwnership(bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var cancellation = new CancellationTokenSource();
        var certified = new MediaWorkerStoppedCancellationException(cancellation.Token);
        supervisor.BeforeReply = _ =>
        {
            source.TryBeginChildUse();
            source.TryEndChildUse();
            cancellation.Cancel();
            throw certified;
        };
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        OperationCanceledException? error = null;
        try
        {
            await PrepareAsync(adapter, source, admitted, cancellation.Token);
        }
        catch (OperationCanceledException caught)
        {
            error = caught;
        }

        error.ShouldNotBeNull().ShouldBeSameAs(certified);
        error.CancellationToken.ShouldBe(cancellation.Token);
        source.ChildBeginSucceeded.ShouldBeTrue();
        source.ChildEndSucceeded.ShouldBeTrue();
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeFalse();
        using var recovered = await fixture.Runtime.AcquireAsync();
        await using var reusable = (await fixture.Files.ReserveOutputAsync(1)).Value;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_CancelAfterStoppedReply_PropagatesCancellationAndReleasesSafeCapacity(bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var cancellation = new CancellationTokenSource();
        supervisor.BeforeReply = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        var error = await Should.ThrowAsync<OperationCanceledException>(() => PrepareAsync(adapter, source, admitted, cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("unconfirmed", false)]
    [InlineData("throw", true)]
    [InlineData("requested-cancel", true)]
    public async Task Prepare_UncertainStop_QuarantinesSourceAndRetainsProcessingAndStagingCapacity(string fault, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var cancellation = new CancellationTokenSource();
        supervisor.BeforeReply = _ =>
        {
            source.TryBeginChildUse();
            if (fault == "unconfirmed")
            { supervisor.Stopped = false; return Task.CompletedTask; }
            if (fault == "requested-cancel")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(new CancellationToken(canceled: true));
            }
            throw new IOException("Stop certificate unavailable.");
        };
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        if (fault == "requested-cancel")
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(() => PrepareAsync(adapter, source, admitted, cancellation.Token));
            error.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            (await PrepareAsync(adapter, source, admitted)).Errors.ShouldBe(new[] { MediaInspectionErrors.Failed(MediaWorkerFailure.Containment) });

        source.DisposeCalls.ShouldBe(0);
        source.OpenReads.ShouldBe(admitted ? 0 : 1);
        source.ReadBytes.ShouldBe(admitted ? 0 : 124);
        source.ChildBegins.ShouldBe(1);
        source.ChildBeginSucceeded.ShouldBeTrue("custody setup assertions must not be swallowed by the containment boundary");
        source.QuarantineCalls.ShouldBeGreaterThan(0);
        source.TryEndChildUse().ShouldBeFalse();
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeFalse();
        await AssertOccupiedAsync(fixture.Runtime);
        await Should.ThrowAsync<ResourceCapacityException>(async () =>
        {
            await using var unexpected = (await fixture.Files.ReserveOutputAsync(1)).Value;
        });
    }

    [Fact]
    public async Task Admission_UnconfirmedBorrowedInspection_DoesNotFreeOuterCustodyWhenBothOwnersDispose()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.BeforeReply = _ => { source.TryBeginChildUse(); supervisor.Stopped = false; return Task.CompletedTask; };
        using var verifier = fixture.Verifier(supervisor);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));
        using var outer = await fixture.Runtime.AcquireAsync(Guid.NewGuid(), "ingestion");
        using var active = outer.Activate();
        using var admission = (await adapter.AdmitAsync(Header(), default)).ShouldNotBeNull();

        (await admission.PrepareAsync(source, "uncertain.chd")).FirstError.Code.ShouldBe("Rom.Media.Containment");
        admission.Dispose();
        outer.Dispose();

        supervisor.SeenLease.ShouldBeSameAs(outer);
        source.ChildBeginSucceeded.ShouldBeTrue();
        outer.Released.ShouldBeFalse();
        File.Exists(outer.RecordPath).ShouldBeTrue();
        source.DisposeCalls.ShouldBe(0);
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeTrue();
        await AssertOccupiedAsync(fixture.Runtime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_ActualAdmittedChildWithInjectedPs2Wire_CarriesProofAfterDistinctChildStop(bool admitted)
    {
        using var child = new MediaWorkerTestFixture("chd-ready");
        using var fixture = new Fixture(child.Options);
        var native = child.ChdCandidate();
        var concrete = new MediaWorkerSupervisor(child.Options, fixture.Runtime, native);
        (await concrete.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        (await concrete.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        var supervisor = new RecordingSupervisor(concrete);
        await using var source = new CountingInput(child.Source(Header(), "chd-ps2-inspected"));
        using var verifier = new ChdVerifier(child.Options, supervisor, fixture.Runtime, native);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        var result = await PrepareAsync(adapter, source, admitted);

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Artifact.ShouldBeSameAs(source);
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        prepared.LogicalFilename.ShouldBe("Disc.Part1.iso");
        var integrity = prepared.ChdIntegrity.ShouldNotBeNull();
        integrity.MatchesMeasurement(prepared.Verification).ShouldBeTrue();
        integrity.Parameters.CapturedEncodingProfile.ShouldBe(prepared.EncodingProfile);
        prepared.Verification.CapturedEncodingProfile.ShouldBe(prepared.EncodingProfile);
        var boot = prepared.ChdPs2Boot.ShouldNotBeNull();
        boot.BootSerial.ShouldBe("SLUS_203.12");
        boot.LogicalBytes.ShouldBe(131072);
        prepared.Hashes.Crc32.ShouldBe(Crc32.FromUInt32(0));
        prepared.Hashes.Crc32.IsEmpty.ShouldBeFalse();
        prepared.Ps2Observation.ShouldBeNull();
        prepared.Eligibility.ShouldBeNull();
        prepared.SourceEquivalence.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
        await prepared.DisposeAsync();
        source.DisposeCalls.ShouldBe(0);
        source.OpenReads.ShouldBe(admitted ? 0 : 1);
        source.ReadBytes.ShouldBe(admitted ? 0 : 124);
        source.ChildBegins.ShouldBe(1);
        source.ChildEnds.ShouldBe(1);
        source.ChildBeginSucceeded.ShouldBeTrue();
        source.ChildEndSucceeded.ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        var execution = supervisor.LastResult.ShouldNotBeNull().Execution.ShouldNotBeNull();
        execution.ProcessId.ShouldNotBe(Environment.ProcessId);
        supervisor.LastResult!.ProcessTreeStopped.ShouldBeTrue();
        MediaWorkerTestFixture.IsRunning(execution.ProcessId).ShouldBeFalse();
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("header-length", true, "Rom.Media.Malformed", "InvalidHeaderLength")]
    [InlineData("parent", true, "Rom.Media.Unsupported", "ParentReference")]
    [InlineData("cd", false, "Rom.Media.Unsupported", "UnsupportedDvdCodec")]
    [InlineData("unknown-unit", true, "Rom.Media.Unsupported", "InvalidDvdGeometry")]
    public async Task Prepare_ActualChildHeaderGuard_RefusesMalformedParentCdAndUnknownGeometryBeforeNativeLoading(
        string defect, bool admitted, string code, string cause)
    {
        using var child = new MediaWorkerTestFixture("chd-ready");
        using var fixture = new Fixture(child.Options);
        var native = child.ChdCandidate();
        var concrete = new MediaWorkerSupervisor(child.Options, fixture.Runtime, native);
        (await concrete.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        (await concrete.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        var supervisor = new RecordingSupervisor(concrete);
        // The mode deliberately bypasses injected CHD reply branches and enters the real child inspection service.
        await using var source = new CountingInput(child.Source(Header(defect), "actual-header-" + defect));
        using var verifier = new ChdVerifier(child.Options, supervisor, fixture.Runtime, native);
        var adapter = new ChdPreparationAdapter(() => verifier, () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        var result = await PrepareAsync(adapter, source, admitted);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(code);
        var raw = supervisor.LastResult.ShouldNotBeNull();
        raw.ProcessTreeStopped.ShouldBeTrue();
        var rejection = raw.Reply.ShouldNotBeNull().ChdRejection.ShouldNotBeNull();
        rejection.Stage.ShouldBe(ChdRejectionStage.Header);
        rejection.Cause.ShouldBe(cause);
        raw.Reply!.NativeIdentity.ShouldBeNull();
        raw.Reply.Inspection.ShouldBeNull();
        raw.Reply.ChdInspection.ShouldBeNull();
        MediaWorkerTestFixture.IsRunning(raw.Execution.ShouldNotBeNull().ProcessId).ShouldBeFalse();
        source.DisposeCalls.ShouldBe(0);
        source.OpenReads.ShouldBe(admitted ? 0 : 1);
        source.ReadBytes.ShouldBe(admitted ? 0 : 124);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    private static async Task<ErrorOr<PreparedRom>> PrepareAsync(ChdPreparationAdapter adapter, ITempFile source,
        bool admitted, CancellationToken ct = default)
    {
        if (!admitted)
            return await adapter.VerifyAsync(source, "Disc.Part1.CHD", ct);
        using var admission = (await adapter.AdmitAsync(Header(), ct)).ShouldNotBeNull();
        return await admission.PrepareAsync(source, "Disc.Part1.CHD", ct);
    }

    private static Task AssertOccupiedAsync(MediaProcessingRuntime runtime, CancellationToken ct = default) =>
        Should.ThrowAsync<ResourceCapacityException>(async () =>
        {
            using var unexpected = await runtime.AcquireAsync(ct: ct);
        });

    private static byte[] Header(string? defect = null)
    {
        byte[] bytes = new byte[128];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), defect == "header-length" ? 123U : 124U);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), 5);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), defect == "cd" ? 0x63647a6cU : 0x7a6c6962U);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32, 8), 131072);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56, 4), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60, 4), defect == "unknown-unit" ? 512U : 2048U);
        if (defect == "parent")
            bytes[104] = 1;
        return bytes;
    }

    private static MediaPs2Inspection Boot() => new(Ps2DvdIdentificationStatus.Identified, RomPs2BootLayoutFacts.ProfileVersion,
        new(131072, 64, 16, 24, 2048, 25, 60, 26, 512,
            "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x100000, 1));

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("romd-chd-adapter-").FullName;
        public MediaProcessingOptions Options { get; }
        public MediaProcessingRuntime Runtime { get; }
        public LocalTempFileFactory Files { get; }
        public ChdNativeCandidate Native { get; }
        public Fixture(MediaProcessingOptions? options = null)
        {
            Options = options ?? new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 };
            var romd = Substitute.For<IRomdOptions>();
            romd.DataDirectory.Returns(_root);
            romd.MaxUploadBytes.Returns(8L << 20);
            Runtime = new(romd, Options);
            Files = new(romd, new FileSystem(), new TempStorageOptions
            { MaximumBytes = 16L << 20, MaximumFiles = 1, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
            Native = new(Path.Combine(_root, "absent-libchdr"),
                new("linux-x64", "libmoonlark_chdr.so", 100, new string('1', 64), new string('4', 40), "test-upstream", 1,
                    "test-family", new string('5', 64), true, true, true));
        }
        public async Task<CountingInput> SourceAsync() => new(await Files.CreateAsync(new MemoryStream(Header())));
        public ChdVerifier Verifier(IMediaWorkerSupervisor supervisor) => new(Options, supervisor, Runtime, Native);
        public InstrumentedSupervisor Supervisor(ITempFile source)
        {
            var identity = new MediaWorkerIdentity(123, 123, Options.MemoryBytes, MediaInspectionService.DecoderBuild,
                "romd-media-worker/mvid:7300d4b9-e9d1-49d0-b6d3-17a09b6dc2b1", OperatingSystem.IsLinux()
                    ? new(Options.LinuxAddressSpaceBytes, Options.LinuxDataBytes, Options.CpuTimeoutSeconds, 0) : null,
                Options.MemoryBytes, 0);
            var measurement = new MediaInspection(source.FileSha256.ToString(), source.FileSize, new string('a', 64),
                new string('b', 40), new string('c', 32), "12345678", 131072, new string('0', 64), "chd-import/v1");
            var inspection = new ChdInspection(5, new(source.FileSize, 131072, 4096, 2048, 32, 0x7a6c6962, 0x6c7a6d61, 0, 0),
                new string('b', 40), new string('b', 40), new string('d', 40), new string('d', 40),
                new(RomChdDvdMetadataFacts.ProfileVersion, 1, 0x44564420, 1, 1, 0, RomChdDvdMetadataFacts.SingletonFingerprint.ToString()));
            return new(new(MediaWorkerStatus.Inspected, identity, measurement, NativeIdentity: Native.ExpectedIdentity, ChdInspection: inspection));
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class InstrumentedSupervisor(MediaWorkerReply reply) : IMediaWorkerSupervisor
    {
        public MediaWorkerReply Reply { get; set; } = reply;
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
            return new(Reply, Failure, new(123, 0, 1024, 0, MediaWorkerDiagnosticKind.None), Stopped);
        }
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => throw new InvalidOperationException("No handshake fallback.");
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No RVZ fallback.");
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No ISO fallback.");
        public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default) => throw new InvalidOperationException("No encoder fallback.");
    }

    private sealed class RecordingSupervisor(MediaWorkerSupervisor inner) : IMediaWorkerSupervisor
    {
        public MediaWorkerResult? LastResult { get; private set; }
        public MediaProcessingRuntime.MediaProcessingReservation? SeenLease { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public async Task<MediaWorkerResult> InspectChdAsync(ITempFile source, CancellationToken ct = default)
        {
            SeenLease = MediaProcessingRuntime.Current;
            var result = await inner.InspectChdAsync(source, ct);
            LastResult = result;
            return result;
        }
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => throw new InvalidOperationException("No handshake fallback.");
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No RVZ fallback.");
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No ISO fallback.");
        public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default) => throw new InvalidOperationException("No encoder fallback.");
    }

    private sealed class CountingInput(ITempFile inner) : ITempFile
    {
        public string Path => inner.Path;
        public Sha256 FileSha256 => inner.FileSha256;
        public long FileSize => inner.FileSize;
        public int OpenReads { get; private set; }
        public long ReadBytes { get; private set; }
        public int DisposeCalls { get; private set; }
        public int ChildBegins { get; private set; }
        public int ChildEnds { get; private set; }
        public bool ChildBeginSucceeded { get; private set; }
        public bool ChildEndSucceeded { get; private set; }
        public int QuarantineCalls { get; private set; }
        public Stream OpenRead()
        {
            OpenReads++;
            return new BoundedHeaderReadStream(inner.OpenRead(), 124, count => ReadBytes += count);
        }
        public bool TryBeginChildUse() { ChildBegins++; return ChildBeginSucceeded = inner.TryBeginChildUse(); }
        public bool TryEndChildUse() { ChildEnds++; return ChildEndSucceeded = inner.TryEndChildUse(); }
        public bool Quarantine() { QuarantineCalls++; return inner.Quarantine(); }
        public ValueTask DisposeAsync() { DisposeCalls++; return inner.DisposeAsync(); }
    }
}
