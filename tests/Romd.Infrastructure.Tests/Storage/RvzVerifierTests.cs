using System.Security.Cryptography;
using System.IO.Abstractions;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RvzVerifierTests
{
    private static readonly SyntheticRvz Disc = new SyntheticRvzBuilder(0x100000, 0x20000).Padding(0x18000, 0x8000, new byte[68]).Build();

    [Fact]
    public async Task VerifyAsync_CompleteDisc_UsesDedicatedChildEvidenceProfile()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var input = fixture.Source(Disc.File);
        using var verifier = new RvzVerifier(new());
        var result = await verifier.VerifyAsync(input);
        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.Verification.Profile.ShouldBe("rvz-disc-supervised/v1");
        result.Value.Container.Version.ShouldBe(0x01000000u);
        result.Value.Container.DiscType.ShouldBe(1u);
        result.Value.Container.Compression.ShouldBe(5u);
        result.Value.Container.BlockBytes.ShouldBe(131072);
        result.Value.Container.LogicalBytes.ShouldBe(Disc.ExpectedDisc.Length);
        result.Value.Container.HasWiiPartitions.ShouldBeFalse();
        result.Value.Verification.VerifierBuild.ShouldStartWith("romd-media-worker/mvid:");
    }

    [Fact]
    public async Task InspectIsoAsync_PspUmdOnNintendoOnlyContract_OmitsPspObservationWhileOriginalIdentificationKeepsIt()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var legacy = fixture.Source(SyntheticPspUmd.Image(), "psp-legacy");
        await using var original = fixture.Source(SyntheticPspUmd.Image(), "psp-original");
        using var verifier = new RvzVerifier(fixture.Options, fixture.Supervisor());

        var nintendoOnly = await verifier.InspectIsoAsync(legacy);
        ErrorOr<RomOriginalMediaIdentification> identified;
        using (var admission = await verifier.AcquireAsync(default))
            identified = await admission.IdentifyOriginalIsoAsync(original, default);

        nintendoOnly.IsError.ShouldBeFalse(nintendoOnly.IsError ? nintendoOnly.FirstError.Description : null);
        nintendoOnly.Value.Verification.Profile.ShouldBe(RvzVerifier.IsoProfile);
        nintendoOnly.Value.Inspection.Layout.ShouldBeNull();
        nintendoOnly.Value.Eligibility.Status.ShouldBe(RomIsoEligibilityStatus.Unsupported);
        identified.IsError.ShouldBeFalse(identified.IsError ? identified.FirstError.Description : null);
        identified.Value.Verification.Profile.ShouldBe(RomPspLayoutFacts.Profile);
        identified.Value.Inspection.Layout.ShouldBe(new RomPspLayoutFacts(original.FileSize, 64, "ULUS12345", "1.00"));
    }

    [Fact]
    public async Task VerifyAsync_CompleteDisc_BindsArtifactOutputHashesAndBuilds()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var input = fixture.Source(Disc.File);
        using var verifier = new RvzVerifier(new());
        var inspected = await verifier.VerifyAsync(input);
        inspected.IsError.ShouldBeFalse(inspected.IsError ? inspected.FirstError.Description : null);
        var result = inspected.Value.Verification;
        result.Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        result.ArtifactSha256.ShouldBe(input.FileSha256);
        result.ArtifactSize.ShouldBe(Disc.File.Length);
        result.ContentSize.ShouldBe(Disc.ExpectedDisc.Length);
        result.ContentSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(Disc.ExpectedDisc)));
        result.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(Disc.ExpectedDisc)));
        result.Md5.ShouldBe(Md5.FromSpan(MD5.HashData(Disc.ExpectedDisc)));
        result.Crc32.ShouldBe(Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(Disc.ExpectedDisc)));
        result.Profile.ShouldBe(RvzVerifier.Profile);
        result.DecoderBuild.ShouldStartWith("romd-media/mvid:");
        result.DecoderBuild.ShouldContain(";zstdsharp/");
        result.DecoderBuild.ShouldContain($";zstdsharp/mvid:{typeof(ZstdSharp.Decompressor).Module.ModuleVersionId}");
        result.DecoderBuild.ShouldContain($";sharpcompress/mvid:{typeof(SharpCompress.Compressors.LZMA.LzmaStream).Module.ModuleVersionId}");
        result.VerifierBuild.ShouldStartWith("romd-media-worker/mvid:");
    }

    [Fact]
    public async Task VerifyAsync_CorruptDisc_IsPermanentWithoutEvidence()
    {
        using var fixture = new MediaWorkerTestFixture();
        byte[] corrupt = Disc.File.ToArray();
        corrupt[0x30] ^= 1;
        await using var input = fixture.Source(corrupt);
        using var verifier = new RvzVerifier(new());
        var result = await verifier.VerifyAsync(input);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.Media.Malformed");
    }

    [Fact]
    public async Task VerifyAsync_LimitsAndChangedArtifact_AreInvalid()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var input = fixture.Source(Disc.File);
        using (var small = new RvzVerifier(new() { MaxArtifactBytes = Disc.File.Length - 1 }))
            (await small.VerifyAsync(input)).FirstError.Code.ShouldBe("Rom.Media.LimitExceeded");
        using (var shallow = new RvzVerifier(new() { MaxLogicalBytes = 0x10000 }))
            (await shallow.VerifyAsync(input)).IsError.ShouldBeTrue();
        await File.WriteAllBytesAsync(input.Path, Disc.File[..^4]);
        using var changed = new RvzVerifier(new());
        (await changed.VerifyAsync(input)).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_Deadline_IsAPermanentFileFailureAndReleasesCapacity()
    {
        // The gated child holds until released, so it reaches the deadline whatever the budget; the same verifier then
        // runs a real inspection, so the budget stays at the fixture's 10 s default sized for that real work.
        using var fixture = new MediaWorkerTestFixture(inspectionMode: "gated");
        await using var input = fixture.Source(Disc.File);
        using var verifier = fixture.Verifier();
        var result = await verifier.VerifyAsync(input);
        result.FirstError.Code.ShouldBe("Rom.Media.WallDeadline");
        fixture.Release();
        (await verifier.VerifyAsync(input)).Value.Verification.ContentSize.ShouldBe(Disc.ExpectedDisc.Length);
    }

    [Fact]
    public async Task VerifyAsync_CancelRunningAndQueuedWork_ReleasesCapacity()
    {
        using var fixture = new MediaWorkerTestFixture(inspectionMode: "gated");
        await using var input = fixture.Source(Disc.File);
        using var verifier = fixture.Verifier();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runningCancellation = new CancellationTokenSource();
        Task running = verifier.VerifyAsync(input, runningCancellation.Token);
        await MediaWorkerTestFixture.WaitForPidAsync(input.Path + ".pid", deadline.Token);
        using var queuedCancellation = new CancellationTokenSource();
        Task queued = verifier.VerifyAsync(input, queuedCancellation.Token);
        await queuedCancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => queued);
        await runningCancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => running);
        fixture.Release();
        (await verifier.VerifyAsync(input)).Value.Verification.ContentSize.ShouldBe(Disc.ExpectedDisc.Length);
    }

    [Fact]
    public async Task VerifyAsync_AdmissionDeadline_DoesNotStartASecondChild()
    {
        using var fixture = new MediaWorkerTestFixture(inspectionMode: "gated");
        await using var input = fixture.Source(Disc.File);
        using var verifier = fixture.Verifier();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        Task running = verifier.VerifyAsync(input, cancellation.Token);
        int pid = await MediaWorkerTestFixture.WaitForPidAsync(input.Path + ".pid", deadline.Token);
        try
        {
            await Should.ThrowAsync<ResourceCapacityException>(() => verifier.VerifyAsync(input));
            int.Parse(await File.ReadAllTextAsync(input.Path + ".pid")).ShouldBe(pid);
            MediaWorkerTestFixture.IsRunning(pid).ShouldBeTrue();
        }
        finally { await cancellation.CancelAsync(); }
        await Should.ThrowAsync<OperationCanceledException>(() => running);
        MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
    }

    [Fact]
    public async Task Admission_DisposedDuringInspection_ReleasesOnlyAfterChildAndDescendantsStop()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var input = fixture.Source(Disc.File, "descendant-hang");
        using var verifier = fixture.Verifier();
        using var admission = await verifier.AcquireAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        Task running = admission.VerifyAsync(input, cancellation.Token);
        int parent = await MediaWorkerTestFixture.WaitForPidAsync(input.Path + ".pid", deadline.Token);
        int descendant = await MediaWorkerTestFixture.WaitForPidAsync(input.Path + ".descendant.pid", deadline.Token);
        try
        {
            admission.Dispose();
            await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
        }
        finally { await cancellation.CancelAsync(); }
        await Should.ThrowAsync<OperationCanceledException>(() => running);
        MediaWorkerTestFixture.IsRunning(parent).ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(descendant).ShouldBeFalse();
        admission.Dispose();
        using var recovered = await verifier.AcquireAsync(default);
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_UnconfirmedContainment_IsPermanentAndNeverReusesReservation(bool cancel)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var input = fixture.Source(Disc.File);
        using var cancellation = new CancellationTokenSource();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, () => new MediaWorkerProcessLifetime((_, _) =>
        {
            if (cancel)
                cancellation.Cancel();
            throw new MediaWorkerCleanupException("fixture stop observation failed");
        }));
        using var verifier = new RvzVerifier(fixture.Options, supervisor);
        if (cancel)
        {
            OperationCanceledException? canceled = null;
            try
            { await verifier.VerifyAsync(input, cancellation.Token); }
            catch (OperationCanceledException error) { canceled = error; }
            canceled.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        }
        else
        {
            var result = await verifier.VerifyAsync(input);
            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe("Rom.Media.Containment");
            result.FirstError.Type.ShouldBe(ErrorType.Validation);
        }
        supervisor.IsAvailable.ShouldBeFalse();
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
    }

    [Theory]
    [InlineData(false, "io")]
    [InlineData(false, "unrequested-cancellation")]
    [InlineData(false, "requested-cancellation")]
    [InlineData(false, "quarantine-io")]
    [InlineData(true, "io")]
    [InlineData(true, "unrequested-cancellation")]
    [InlineData(true, "requested-cancellation")]
    [InlineData(true, "quarantine-io")]
    public async Task Inspection_UnexpectedSupervisorThrow_QuarantinesActualSourceAndNeverReusesReservation(bool iso, string failure)
    {
        string root = Directory.CreateTempSubdirectory("romd-verifier-unknown-stop-").FullName;
        try
        {
            var files = Files(root);
            var actual = await files.CreateAsync(new MemoryStream(iso ? SyntheticNintendoIso.GameCube() : Disc.File));
            ITempFile source = failure == "quarantine-io" ? new ThrowingQuarantineInput(actual) : actual;
            using var cancellation = new CancellationTokenSource();
            var supervisor = new UnknownStopSupervisor(failure, cancellation);
            using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 }, supervisor);
            async Task<string> InspectFailureAsync(CancellationToken ct)
            {
                if (iso)
                {
                    var result = await verifier.InspectIsoAsync(source, ct);
                    result.IsError.ShouldBeTrue();
                    result.FirstError.Type.ShouldBe(ErrorType.Validation);
                    return result.FirstError.Code;
                }
                var inspected = await verifier.VerifyAsync(source, ct);
                inspected.IsError.ShouldBeTrue();
                inspected.FirstError.Type.ShouldBe(ErrorType.Validation);
                return inspected.FirstError.Code;
            }
            try
            {
                if (failure == "requested-cancellation")
                {
                    OperationCanceledException? observed = null;
                    try
                    { await InspectFailureAsync(cancellation.Token); }
                    catch (OperationCanceledException error) { observed = error; }
                    observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
                }
                else
                    (await InspectFailureAsync(cancellation.Token)).ShouldBe("Rom.Media.Containment");
                source.TryEndChildUse().ShouldBeFalse("unknown stop must permanently quarantine the source");
            }
            finally { await source.DisposeAsync(); }
            supervisor.Calls.ShouldBe(1);
            File.Exists(source.Path).ShouldBeTrue();
            File.Exists(source.Path + ".quarantine").ShouldBeTrue();
            await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
            await Should.ThrowAsync<ResourceCapacityException>(() => files.ReserveOutputAsync(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspection_CanceledBeforeReservationRuns_DoesNotLaunchOrQuarantineAndReleasesUnstartedSlot(bool iso)
    {
        string root = Directory.CreateTempSubdirectory("romd-verifier-unstarted-").FullName;
        try
        {
            var files = Files(root);
            await using var source = await files.CreateAsync(new MemoryStream(iso ? SyntheticNintendoIso.GameCube() : Disc.File));
            using var cancellation = new CancellationTokenSource();
            var supervisor = new UnknownStopSupervisor("io", cancellation);
            using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 }, supervisor);
            using var admission = await verifier.AcquireAsync(default);
            await cancellation.CancelAsync();
            OperationCanceledException? observed = null;
            try
            {
                if (iso)
                    await admission.InspectIsoAsync(source, cancellation.Token);
                else
                    await admission.VerifyAsync(source, cancellation.Token);
            }
            catch (OperationCanceledException error) { observed = error; }
            observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            supervisor.Calls.ShouldBe(0);
            File.Exists(source.Path + ".quarantine").ShouldBeFalse();
            await source.DisposeAsync();
            File.Exists(source.Path).ShouldBeFalse();
            using var recovered = await verifier.AcquireAsync(default);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspection_CertifiedStoppedCancellation_PreservesExceptionAndReleasesActualSourceAndAdmission(bool iso)
    {
        string root = Directory.CreateTempSubdirectory("romd-verifier-certified-stop-").FullName;
        try
        {
            var files = Files(root);
            await using var source = await files.CreateAsync(new MemoryStream(iso ? SyntheticNintendoIso.GameCube() : Disc.File));
            using var cancellation = new CancellationTokenSource();
            var supervisor = new UnknownStopSupervisor("certified-cancellation", cancellation);
            using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 }, supervisor);
            OperationCanceledException? observed = null;
            try
            {
                if (iso)
                    await verifier.InspectIsoAsync(source, cancellation.Token);
                else
                    await verifier.VerifyAsync(source, cancellation.Token);
            }
            catch (OperationCanceledException error) { observed = error; }
            observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            observed.ShouldBeSameAs(supervisor.CertifiedCancellation);
            supervisor.Calls.ShouldBe(1);
            File.Exists(source.Path + ".quarantine").ShouldBeFalse();
            await source.DisposeAsync();
            File.Exists(source.Path).ShouldBeFalse();
            using var recovered = await verifier.AcquireAsync(default);
            await using var reusable = (await files.ReserveOutputAsync(1)).Value;
        }
        finally { Directory.Delete(root, true); }
    }

    private static LocalTempFileFactory Files(string root)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(root);
        options.MaxUploadBytes.Returns(8L << 20);
        return new(options, new FileSystem(), new TempStorageOptions
        { MaximumBytes = 16L << 20, MaximumFiles = 1, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
    }
}

/// <summary>Exercises actual custody and supervisor throws with or without affirmative stop proof.</summary>
internal sealed class UnknownStopSupervisor(string failure, CancellationTokenSource cancellation) : IMediaWorkerSupervisor
{
    public int Calls { get; private set; }
    public MediaWorkerStoppedCancellationException? CertifiedCancellation { get; private set; }
    public bool IsAvailable => true;
    public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Startup));
    public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => ThrowAsync(source, null, ct);
    public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => ThrowAsync(source, null, ct);
    public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default) =>
        ThrowAsync(source, output, ct);

    private Task<MediaWorkerResult> ThrowAsync(ITempFile source, IWritableTempFile? output, CancellationToken ct)
    {
        Calls++;
        source.TryBeginChildUse().ShouldBeTrue();
        if (output is not null)
        {
            output.TryBeginChildUse().ShouldBeTrue();
            using var payload = new FileStream(output.Path, FileMode.CreateNew, FileAccess.Write);
            payload.WriteByte(1);
        }
        if (failure == "certified-cancellation")
        {
            source.TryEndChildUse().ShouldBeTrue();
            if (output is not null)
                output.TryEndChildUse().ShouldBeTrue();
            cancellation.Cancel();
            CertifiedCancellation = new(cancellation.Token);
            return Task.FromException<MediaWorkerResult>(CertifiedCancellation);
        }
        if (failure == "requested-cancellation")
            cancellation.Cancel();
        Exception error = failure is "io" or "quarantine-io" ? new IOException("fixture supervisor failed without a stop certificate")
            : new OperationCanceledException(new CancellationToken(canceled: true));
        return Task.FromException<MediaWorkerResult>(error);
    }
}

internal sealed class ThrowingQuarantineInput(ITempFile input) : ITempFile
{
    public string Path => input.Path;
    public Sha256 FileSha256 => input.FileSha256;
    public long FileSize => input.FileSize;
    public bool TryBeginChildUse() => input.TryBeginChildUse();
    public bool TryEndChildUse() => input.TryEndChildUse();
    public bool Quarantine()
    {
        input.Quarantine();
        throw new IOException("fixture quarantine reporting failed after retaining custody");
    }
    public Stream OpenRead() => input.OpenRead();
    public ValueTask DisposeAsync() => input.DisposeAsync();
}
