using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomOriginalMediaIdentifierTests
{
    [Theory]
    [InlineData(false, 0, RomIsoDiscKind.GameCube)]
    [InlineData(true, 0, RomIsoDiscKind.Wii)]
    [InlineData(true, 1, RomIsoDiscKind.Wii)]
    [InlineData(true, 2, null)]
    public async Task IdentifyAsync_OriginalBytes_ReturnsContentBoundFactsWithoutRemintingObservation(bool wii, int key, RomIsoDiscKind? kind)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(wii ? SyntheticNintendoIso.Wii((byte)key) : SyntheticNintendoIso.GameCube());
        using var verifier = fixture.Verifier();
        var result = await new RomOriginalMediaIdentifier(verifier).IdentifyAsync(source);

        result.IsError.ShouldBeFalse();
        var proof = result.Value.Verification;
        var eligibility = result.Value.Eligibility.ShouldNotBeNull();
        proof.ArtifactSha256.ShouldBe(source.FileSha256);
        proof.ContentSha256.ShouldBe(source.FileSha256);
        proof.ArtifactSize.ShouldBe(source.FileSize);
        proof.ContentSize.ShouldBe(source.FileSize);
        proof.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        proof.Profile.ShouldBe(RvzVerifier.IsoProfile);
        proof.VerifierBuild.ShouldStartWith("romd-media-worker/mvid:");
        eligibility.ContentSha256.ShouldBe(proof.ContentSha256);
        eligibility.ContentSize.ShouldBe(proof.ContentSize);
        eligibility.InspectorBuild.ShouldBe(proof.DecoderBuild);
        eligibility.InspectedAt.ShouldBe(proof.VerifiedAt);
        eligibility.DiscKind.ShouldBe(kind);
        eligibility.Status.ShouldBe(kind is null ? RomIsoEligibilityStatus.Unsupported : RomIsoEligibilityStatus.Supported);
        eligibility.WiiPartitionCount.ShouldBe(wii && kind is not null ? 1 : 0);
        eligibility.StandardKeyPartitions.ShouldBe(wii && key == 0 ? 1 : 0);
        eligibility.KoreanKeyPartitions.ShouldBe(wii && key == 1 ? 1 : 0);
        result.Value.Ps2Observation.ShouldBeNull();
    }

    [Fact]
    public async Task IdentifyAsync_Ps2Original_BindsBootToCompleteStoppedMeasurementWithoutDvdClaim()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticPs2Iso.Create());
        using var verifier = fixture.Verifier();

        var result = await new RomOriginalMediaIdentifier(verifier).IdentifyAsync(source);

        result.IsError.ShouldBeFalse();
        result.Value.Eligibility.ShouldBeNull();
        var observation = result.Value.Ps2Observation.ShouldNotBeNull();
        observation.Measurement.ShouldBeSameAs(result.Value.Verification);
        observation.Measurement.ContentSha256.ShouldBe(source.FileSha256);
        observation.Measurement.ArtifactSha256.ShouldBe(source.FileSha256);
        observation.Measurement.ContentSize.ShouldBe(source.FileSize);
        observation.Measurement.ArtifactSize.ShouldBe(source.FileSize);
        observation.Measurement.Sha1.ShouldBe(Sha1.FromSpan(System.Security.Cryptography.SHA1.HashData(SyntheticPs2Iso.Create())));
        observation.Measurement.Md5.ShouldBe(Md5.FromSpan(System.Security.Cryptography.MD5.HashData(SyntheticPs2Iso.Create())));
        observation.Measurement.Crc32.ShouldBe(Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(SyntheticPs2Iso.Create())));
        observation.Measurement.Profile.ShouldBe(RomPs2BootObservation.MeasurementProfile);
        observation.Measurement.VerifierBuild.ShouldStartWith("romd-media-worker/mvid:");
        observation.Boot.InspectorBuild.ShouldBe(observation.Measurement.DecoderBuild);
        observation.Boot.BootSerial.ShouldBe(SyntheticPs2Iso.BootSerial);
        observation.Boot.LogicalBytes.ShouldBe(source.FileSize);
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        using var recovered = await verifier.AcquireAsync(default);
    }

    [Fact]
    public async Task IdentifyAsync_UnrecognizedNonNintendoIso_DoesNotInventNegativeEligibility()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(new byte[131072]);
        using var verifier = fixture.Verifier();

        var result = await new RomOriginalMediaIdentifier(verifier).IdentifyAsync(source);

        result.IsError.ShouldBeFalse();
        result.Value.Eligibility.ShouldBeNull();
        result.Value.Ps2Observation.ShouldBeNull();
        result.Value.Verification.ContentSha256.ShouldBe(source.FileSha256);
        result.Value.Verification.ContentSize.ShouldBe(source.FileSize);
    }

    [Fact]
    public async Task AcquireAsync_UnusedAndCompletedAdmissions_ReleaseCapacityAndRemainSingleUse()
    {
        using var fixture = new MediaWorkerTestFixture();
        using var verifier = fixture.Verifier();
        var identifier = new RomOriginalMediaIdentifier(verifier);
        var unused = await identifier.AcquireAsync();
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
        unused.Dispose();
        using var admission = await identifier.AcquireAsync();
        await using var source = fixture.Source(SyntheticNintendoIso.GameCube());
        (await admission.IdentifyAsync(source)).IsError.ShouldBeFalse();
        using var recovered = await verifier.AcquireAsync(default);
        await Should.ThrowAsync<InvalidOperationException>(() => admission.IdentifyAsync(source));
        await Should.ThrowAsync<InvalidOperationException>(() => unused.IdentifyAsync(source));
    }

    [Fact]
    public async Task IdentifyAsync_StoppedFailure_PreservesErrorAndReleasesAdmission()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticNintendoIso.GameCube());
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        using var cancellation = new CancellationTokenSource();
        supervisor.InspectIsoAsync(source, cancellation.Token).Returns(MediaWorkerResult.Failed(MediaWorkerFailure.Malformed));
        using var verifier = new RvzVerifier(fixture.Options, supervisor);
        var result = await new RomOriginalMediaIdentifier(verifier).IdentifyAsync(source, cancellation.Token);
        result.Errors.ShouldBe(new[] { MediaInspectionErrors.Failed(MediaWorkerFailure.Malformed) });
        await supervisor.Received(1).InspectIsoAsync(source, cancellation.Token);
        using var recovered = await verifier.AcquireAsync(default);
    }

    [Fact]
    public async Task IdentifyAsync_CertifiedCancellation_PreservesExactExceptionAndReleasesAdmission()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticNintendoIso.GameCube());
        using var cancellation = new CancellationTokenSource();
        var supervisor = new UnknownStopSupervisor("certified-cancellation", cancellation);
        using var verifier = new RvzVerifier(fixture.Options, supervisor);
        OperationCanceledException? exception = null;
        try
        { await new RomOriginalMediaIdentifier(verifier).IdentifyAsync(source, cancellation.Token); }
        catch (OperationCanceledException observed) { exception = observed; }
        exception.ShouldNotBeNull().ShouldBeSameAs(supervisor.CertifiedCancellation);
        exception.CancellationToken.ShouldBe(cancellation.Token);
        using var recovered = await verifier.AcquireAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_ExplicitIdentifier_UsesSharedFactsAndCallerFilename(bool admitted)
    {
        using var fixture = new MediaWorkerTestFixture();
        byte[] bytes = SyntheticNintendoIso.GameCube();
        await using var source = fixture.Source(bytes);
        using var verifier = fixture.Verifier();
        var facts = (await new RomOriginalMediaIdentifier(verifier).IdentifyAsync(source)).Value;
        var identifier = Substitute.For<IRomOriginalMediaIdentifier>();
        var admission = Substitute.For<IRomOriginalMediaIdentificationAdmission>();
        using var cancellation = new CancellationTokenSource();
        identifier.AcquireAsync(cancellation.Token).Returns(admission);
        identifier.IdentifyAsync(source, cancellation.Token).Returns(facts);
        admission.IdentifyAsync(source, cancellation.Token).Returns(facts);
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("Original identification must use the shared port."),
            () => identifier,
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));
        ErrorOr<PreparedRom> result;
        if (admitted)
        {
            using var slot = (await service.AdmitAsync(bytes.AsMemory(0, 32), "Caller.GCM", cancellation.Token)).ShouldNotBeNull();
            result = await slot.PrepareAsync(source, "Caller.GCM", cancellation.Token);
        }
        else
            result = await service.PrepareAsync(source, "Caller.GCM", cancellation.Token);
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Verification.ShouldBeSameAs(facts.Verification);
        prepared.Eligibility.ShouldBeSameAs(facts.Eligibility);
        prepared.LogicalFilename.ShouldBe("Caller.iso");
        prepared.Artifact.ShouldBeSameAs(source);
        if (admitted)
        {
            await identifier.Received(1).AcquireAsync(cancellation.Token);
            await admission.Received(1).IdentifyAsync(source, cancellation.Token);
            admission.Received(1).Dispose();
            await identifier.DidNotReceiveWithAnyArgs().IdentifyAsync(default!, default);
        }
        else
            await identifier.Received(1).IdentifyAsync(source, cancellation.Token);
    }


}
