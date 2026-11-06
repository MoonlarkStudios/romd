using System.Security.Cryptography;
using ErrorOr;
using System.IO.Abstractions;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Nintendo;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class IsoPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_Ps2Original_ResolvesExactReferenceAfterInspectionOnBothPaths(bool admitted)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var facts = Ps2Facts(bytes);
        await using var source = new PrefixOnlySource(bytes);
        var store = new ReferenceStore([Reference(facts.Verification)]);
        var service = Ps2Service(facts, store);
        ErrorOr<PreparedRom> result;

        if (admitted)
        {
            using var slot = (await service.AdmitAsync(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso")).ShouldNotBeNull();
            result = await slot.PrepareAsync(source, "disc.iso");
        }
        else
            result = await service.PrepareAsync(source, "disc.iso");

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Verification.ShouldBeSameAs(facts.Verification);
        prepared.Verification.Id.ShouldBe(facts.Verification.Id);
        prepared.Ps2Observation.ShouldBeSameAs(facts.Ps2Observation);
        prepared.Artifact.ShouldBeSameAs(source);
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        prepared.EncodingProfile.ShouldBe(RomEncodingProfiles.Identity);
        prepared.Hashes.Sha1.ShouldBe(facts.Verification.Sha1);
        prepared.Hashes.Md5.ShouldBe(facts.Verification.Md5);
        prepared.Hashes.Crc32.ShouldBe(facts.Verification.Crc32);
        prepared.Hashes.BytesRead.ShouldBe(bytes.Length);
        var eligibility = prepared.Eligibility.ShouldNotBeNull();
        eligibility.DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
        eligibility.Status.ShouldBe(RomIsoEligibilityStatus.Supported);
        eligibility.InspectedAt.ShouldBe(facts.Verification.VerifiedAt);
        eligibility.InspectorBuild.ShouldBe(facts.Verification.DecoderBuild);
        var details = eligibility.Ps2DvdFacts.ShouldNotBeNull();
        details.Boot.ShouldBeSameAs(facts.Ps2Observation!.Boot);
        details.DvdReference.ReferenceSha256.ShouldBe(store.Response.Value[0].RawReferenceSha256);
        store.Calls.ShouldBe(1);
        store.LastQuery.ShouldBe(new RomDiscMediaReferenceQuery(bytes.Length, facts.Verification.Sha1,
            facts.Verification.Md5, facts.Verification.Crc32));
        store.LastLimit.ShouldBe(RomPs2DvdEligibilityResolver.MaximumReferenceRecords + 1);
        source.OpenReads.ShouldBe(admitted ? 0 : 1); // Resolution never opens the already measured original.
        source.ReadBytes.ShouldBe(admitted ? 0 : RomPreparationDetection.MaximumPrefixBytes);
        source.Disposed.ShouldBeFalse(); // Prepared original is borrowed, including its resolved wrapper.
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    [InlineData("conflict", false)]
    [InlineData("conflict", true)]
    public async Task PrepareAsync_PendingPs2Reference_RetriesSameObservationWithoutNegativeCache(string reason, bool admitted)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var facts = Ps2Facts(bytes);
        await using var source = new PrefixOnlySource(bytes, maximumOpens: 2);
        var store = new ReferenceStore(reason == "conflict"
            ? [Reference(facts.Verification), Reference(facts.Verification, RomDiscReferenceMedia.Dvd9)] : []);
        if (reason == "unavailable")
            store.Response = RomDiscMediaReferenceErrors.Unavailable();
        var service = Ps2Service(facts, store);

        async Task<ErrorOr<PreparedRom>> Prepare()
        {
            if (!admitted)
                return await service.PrepareAsync(source, "disc.iso");
            using var slot = (await service.AdmitAsync(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso")).ShouldNotBeNull();
            return await slot.PrepareAsync(source, "disc.iso");
        }
        await using var pending = (await Prepare()).Value.ShouldBeOfType<PreparedRom.WholeFile>();
        pending.Eligibility.ShouldBeNull();
        pending.Verification.ShouldBeSameAs(facts.Verification);
        pending.Ps2Observation.ShouldBeSameAs(facts.Ps2Observation);
        store.SetRows([Reference(facts.Verification)]);
        await using var resolved = (await Prepare()).Value.ShouldBeOfType<PreparedRom.WholeFile>();

        resolved.Eligibility.ShouldNotBeNull().DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
        resolved.Verification.ShouldBeSameAs(pending.Verification);
        resolved.Ps2Observation.ShouldBeSameAs(pending.Ps2Observation);
        pending.Eligibility.ShouldBeNull();
        store.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_Ps2ReferenceError_PropagatesExactErrorWithoutPublishing(bool admitted)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var facts = Ps2Facts(bytes);
        await using var source = new PrefixOnlySource(bytes);
        var expected = Error.Validation("Reference.InvalidImport", "Invalid trusted import");
        var store = new ReferenceStore([]) { Response = expected };
        var service = Ps2Service(facts, store);
        ErrorOr<PreparedRom> result;
        if (admitted)
        {
            using var slot = (await service.AdmitAsync(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso")).ShouldNotBeNull();
            result = await slot.PrepareAsync(source, "disc.iso");
        }
        else
            result = await service.PrepareAsync(source, "disc.iso");

        result.Errors.ShouldBe(new[] { expected });
        source.Disposed.ShouldBeFalse();
        store.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_CancelDuringPs2ReferenceLookup_PropagatesCallerCancellationWithoutPublishing(bool admitted)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var facts = Ps2Facts(bytes);
        await using var source = new PrefixOnlySource(bytes);
        using var cancellation = new CancellationTokenSource();
        var store = new ReferenceStore([Reference(facts.Verification)]) { OnFind = () => cancellation.Cancel() };
        var service = Ps2Service(facts, store);
        using var slot = admitted ? (await service.AdmitAsync(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso", cancellation.Token)).ShouldNotBeNull() : null;
        try
        {
            if (slot is null)
                await service.PrepareAsync(source, "disc.iso", cancellation.Token);
            else
                await slot.PrepareAsync(source, "disc.iso", cancellation.Token);
        }
        catch (OperationCanceledException exception)
        {
            exception.CancellationToken.ShouldBe(cancellation.Token);
            store.LastToken.ShouldBe(cancellation.Token);
            store.Calls.ShouldBe(1);
            source.Disposed.ShouldBeFalse();
            return;
        }
        throw new InvalidOperationException("Expected caller cancellation from reference resolution.");
    }

    [Fact]
    public void PreparedRom_ForeignMeasurementBootObservation_RejectsEvenWhenAllBytesMatch()
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var first = Ps2Facts(bytes);
        var second = Ps2Facts(bytes); // Complete bytes, builds and timestamp match; the observation ID differs.
        var source = new PrefixOnlySource(bytes);
        Should.Throw<ArgumentException>(() => new PreparedRom.WholeFile(source, "disc.iso", first.Verification.ContentSha256,
            new(first.Verification.Sha1, first.Verification.Md5, first.Verification.Crc32, bytes.Length),
            RomRepresentationEncoding.Identity, first.Verification, RomContainerFacts.Identity.Instance, ps2Observation: second.Ps2Observation));
        Should.Throw<ArgumentException>(() => new RomOriginalMediaIdentification(first.Verification, first.Inspection, null, second.Ps2Observation));
        source.OpenReads.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_PositivePs2FactsWithUnavailableReferenceStore_RetainsObservationWithoutNegativeEligibility()
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var facts = Ps2Facts(bytes);
        await using var source = new PrefixOnlySource(bytes);
        var identifier = Substitute.For<IRomOriginalMediaIdentifier>();
        identifier.IdentifyAsync(source, Arg.Any<CancellationToken>()).Returns(facts);
        var references = Substitute.For<IRomDiscMediaReferenceStore>();
        references.FindAsync(Arg.Any<RomDiscMediaReferenceQuery>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(RomDiscMediaReferenceErrors.Unavailable());
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("Use the supplied original identifier."),
            () => identifier,
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(references));

        var result = await service.PrepareAsync(source, "disc.iso");

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<PreparedRom.WholeFile>();
        prepared.Artifact.ShouldBeSameAs(source);
        prepared.Verification.ShouldBeSameAs(facts.Verification);
        prepared.Inspection.ShouldBeSameAs(facts.Inspection);
        prepared.Ps2Observation.ShouldBeSameAs(facts.Ps2Observation);
        prepared.Ps2Observation.ShouldNotBeNull();
        prepared.Eligibility.ShouldBeNull();
        source.Disposed.ShouldBeFalse();
        await references.Received(1).FindAsync(Arg.Any<RomDiscMediaReferenceQuery>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_NonPs2Iso9660Candidate_ConsumesAdmissionWithoutInventingEligibilityOrQueryingReferences()
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        bytes.AsSpan(SyntheticPs2Iso.SystemCnfOffset, SyntheticPs2Iso.SectorBytes).Clear();
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(bytes);
        using var verifier = fixture.Verifier();
        var store = new ReferenceStore([]);
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => verifier,
            () => new RomOriginalMediaIdentifier(((Func<RvzVerifier>)(() => verifier))()),
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new RomPs2DvdEligibilityResolver(store));
        service.RequiresDatHashes(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso").ShouldBeFalse();
        using var admission = (await service.AdmitAsync(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso")).ShouldNotBeNull();

        await using var prepared = (await admission.PrepareAsync(source, "disc.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Eligibility.ShouldBeNull();
        prepared.Ps2Observation.ShouldBeNull();
        prepared.ContentSha256.ShouldBe(source.FileSha256);
        store.Calls.ShouldBe(0);
        using var recovered = await verifier.AcquireAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_NintendoWithScopedResolver_PreservesEligibilityWithoutReferenceLookup(bool admitted)
    {
        byte[] bytes = SyntheticNintendoIso.GameCube();
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(bytes);
        using var verifier = fixture.Verifier();
        var store = new ReferenceStore([]) { OnFind = () => throw new InvalidOperationException("Nintendo must not query DVD references.") };
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => verifier,
            () => new RomOriginalMediaIdentifier(((Func<RvzVerifier>)(() => verifier))()),
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new RomPs2DvdEligibilityResolver(store));
        using var slot = admitted ? (await service.AdmitAsync(bytes.AsMemory(0, 32), "disc.gcm")).ShouldNotBeNull() : null;
        await using var prepared = (await (slot is null ? service.PrepareAsync(source, "disc.gcm") : slot.PrepareAsync(source, "disc.gcm"))).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Eligibility!.DiscKind.ShouldBe(RomIsoDiscKind.GameCube);
        prepared.LogicalFilename.ShouldBe("disc.iso");
        prepared.Ps2Observation.ShouldBeNull();
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_RvzWithScopedResolver_PreservesOriginalArtifactWithoutReferenceLookup()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(new SyntheticRvzBuilder(0x100000, 0x20000).Build().File);
        using var verifier = fixture.Verifier();
        var store = new ReferenceStore([]) { OnFind = () => throw new InvalidOperationException("RVZ must not query DVD references.") };
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => verifier,
            () => new RomOriginalMediaIdentifier(((Func<RvzVerifier>)(() => verifier))()),
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new RomPs2DvdEligibilityResolver(store));

        await using var prepared = (await service.PrepareAsync(source, "disc.rvz")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        prepared.Artifact.ShouldBeSameAs(source);
        prepared.Verification.Profile.ShouldBe(RvzVerifier.Profile);
        prepared.Eligibility.ShouldBeNull();
        prepared.Ps2Observation.ShouldBeNull();
        store.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_RealPs2Inspection_ReleasesMediaAdmissionBeforeLocalReferenceLookup(bool admitted)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(bytes);
        using var verifier = fixture.Verifier();
        var reference = Reference(Ps2Facts(bytes).Verification);
        var store = new ReferenceStore([reference])
        {
            DuringFind = async () =>
            {
                using var recovered = await verifier.AcquireAsync(default);
            }
        };
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => verifier,
            () => new RomOriginalMediaIdentifier(((Func<RvzVerifier>)(() => verifier))()),
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new RomPs2DvdEligibilityResolver(store));
        using var slot = admitted ? (await service.AdmitAsync(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso")).ShouldNotBeNull() : null;

        await using var prepared = (await (slot is null ? service.PrepareAsync(source, "disc.iso") : slot.PrepareAsync(source, "disc.iso"))).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Eligibility.ShouldNotBeNull().DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
        prepared.Ps2Observation.ShouldNotBeNull().Measurement.ShouldBeSameAs(prepared.Verification);
        store.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(MediaWorkerFailure.Malformed)]
    [InlineData(MediaWorkerFailure.Protocol)]
    [InlineData(MediaWorkerFailure.LimitExceeded)]
    public async Task PrepareAsync_FailedPs2Inspection_PreservesTypedFailureWithoutQueryingReferences(MediaWorkerFailure failure)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticPs2Iso.Create());
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        supervisor.InspectIsoAsync(source, Arg.Any<CancellationToken>()).Returns(MediaWorkerResult.Failed(failure));
        using var verifier = new RvzVerifier(fixture.Options, supervisor);
        var store = new ReferenceStore([]);
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => verifier,
            () => new RomOriginalMediaIdentifier(((Func<RvzVerifier>)(() => verifier))()),
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new RomPs2DvdEligibilityResolver(store));

        var result = await service.PrepareAsync(source, "disc.iso");

        result.FirstError.Code.ShouldBe("Rom.Media." + failure);
        store.Calls.ShouldBe(0);
        using var recovered = await verifier.AcquireAsync(default);
    }

    [Theory]
    [InlineData("wrong-signature")]
    [InlineData("wrong-version")]
    [InlineData("wrong-type")]
    [InlineData("short")]
    [InlineData("gcm")]
    [InlineData("wii")]
    public void RequiresDatHashes_DescriptorBoundaryRejectsNonCandidates(string changed)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        if (changed == "wrong-signature")
            bytes[16 * 2048 + 1] = (byte)'X';
        if (changed == "wrong-version")
            bytes[16 * 2048 + 6] = 2;
        if (changed == "wrong-type")
            bytes[16 * 2048] = 255;
        string filename = changed is "gcm" or "wii" ? "disc." + changed : "disc.iso";
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => throw new InvalidOperationException("Detection cannot resolve a worker."), new MediaInfrastructureTestServices.EmptyReferences());

        service.RequiresDatHashes(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes - (changed == "short" ? 1 : 0)), filename).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_AgreeingEarlierReference_RecomputesFromCurrentAssertionsWithoutCachedEligibility(bool admitted)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var facts = Ps2Facts(bytes);
        var retained = Reference(facts.Verification);
        var earlier = new RomDiscMediaReferenceRecord(Guid.NewGuid(), retained.System, retained.Media, retained.FileCount,
            retained.LogicalBytes, retained.Sha1, retained.Md5, retained.Crc32, retained.RawReferenceBytes,
            Sha256.Parse(new string('b', 64)), "earlier-sorting-reference", retained.ParserProfile, retained.AcceptedSourceProvenance, retained.RetrievedAt);
        (earlier.RawReferenceSha256 < retained.RawReferenceSha256).ShouldBeTrue();
        var store = new ReferenceStore([retained]);
        var service = Ps2Service(facts, store);
        await using var source = new PrefixOnlySource(bytes, maximumOpens: 2);
        async Task<PreparedRom.WholeFile> Prepare()
        {
            using var slot = admitted ? (await service.AdmitAsync(bytes.AsMemory(0, RomPreparationDetection.MaximumPrefixBytes), "disc.iso")).ShouldNotBeNull() : null;
            return (await (slot is null ? service.PrepareAsync(source, "disc.iso") : slot.PrepareAsync(source, "disc.iso"))).Value.ShouldBeOfType<PreparedRom.WholeFile>();
        }
        await using var first = await Prepare();
        first.Eligibility.ShouldNotBeNull().Ps2DvdFacts!.DvdReference.ReferenceRecordId.ShouldBe(retained.Id);
        store.SetRows([earlier, retained]);
        await using var current = await Prepare();
        current.Eligibility.ShouldNotBeNull().Ps2DvdFacts!.DvdReference.ReferenceRecordId.ShouldBe(earlier.Id);
        first.Eligibility!.Ps2DvdFacts!.DvdReference.ReferenceRecordId.ShouldBe(retained.Id);
        current.Inspection.ShouldBeSameAs(first.Inspection);
        current.Verification.ShouldBeSameAs(first.Verification);
        current.Ps2Observation.ShouldBeSameAs(facts.Ps2Observation);
        store.Calls.ShouldBe(2);
        source.OpenReads.ShouldBe(admitted ? 0 : 2);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("same")]
    [InlineData("sha256")]
    [InlineData("size")]
    [InlineData("boot")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc")]
    public async Task PreparedRom_SuppliedPs2Eligibility_RequiresExactOriginalBootAndLogicalIdentity(string changed)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        var facts = Ps2Facts(bytes);
        var measured = facts.Verification;
        await using var source = new PrefixOnlySource(bytes);
        var originalBoot = facts.Ps2Observation.ShouldNotBeNull().Boot;
        long cachedSize = changed == "size" ? measured.ContentSize + 2048 : measured.ContentSize;
        var cachedHash = changed == "sha256" ? Sha256.Parse(new string('f', 64)) : measured.ContentSha256;
        var cachedSha1 = changed == "sha1" ? Sha1.Parse(new string('f', 40)) : measured.Sha1;
        var cachedMd5 = changed == "md5" ? Md5.Parse(new string('f', 32)) : measured.Md5;
        var cachedCrc = changed == "crc" ? Crc32.FromUInt32(measured.Crc32.ToUInt32() ^ 1) : measured.Crc32;
        var cachedBoot = new RomPs2BootLayoutFacts(cachedSize, originalBoot.VolumeBlocks, originalBoot.PrimaryDescriptorBlock,
            originalBoot.RootDirectoryBlock, originalBoot.RootDirectoryBytes, originalBoot.SystemCnfBlock, originalBoot.SystemCnfBytes,
            originalBoot.BootExecutableBlock, originalBoot.BootExecutableBytes, originalBoot.ObservedBoot2Value, originalBoot.BootPath,
            originalBoot.BootSerial, changed == "boot" ? originalBoot.EntryPoint + 4 : originalBoot.EntryPoint,
            originalBoot.ProgramHeaderCount, originalBoot.Profile, originalBoot.InspectorBuild);
        var reference = Reference(measured);
        var dvd = new RomDvdMediaReference(reference.Id, cachedHash, cachedSize, cachedSha1, cachedMd5, cachedCrc,
            RomDvdMediaKind.Dvd5, reference.ParserProfile, reference.RawReferenceSha256, reference.ReferenceSource);
        var cached = new RomIsoEligibility(cachedHash, cachedSize, RomPs2DvdEligibilityResolver.Adapter,
            RomPs2DvdEligibilityFacts.ProfileVersion, cachedBoot.InspectorBuild, RomIsoEligibilityStatus.Supported,
            RomIsoDiscKind.PlayStation2Dvd, 0, 0, 0, measured.VerifiedAt,
            new(cachedHash, cachedSize, cachedSha1, cachedMd5, cachedCrc, cachedBoot, dvd));

        PreparedRom.WholeFile Prepare() => new(source, "disc.iso", measured.ContentSha256,
            new(measured.Sha1, measured.Md5, measured.Crc32, measured.ContentSize), measured.Encoding, measured, RomContainerFacts.Identity.Instance,
            eligibility: cached, ps2Observation: facts.Ps2Observation);
        if (changed == "same")
        {
            await using var prepared = Prepare();
            prepared.Eligibility.ShouldBeSameAs(cached);
            prepared.Verification.ShouldBeSameAs(measured);
            prepared.Ps2Observation.ShouldBeSameAs(facts.Ps2Observation);
            cached.InspectedAt.ShouldBe(measured.VerifiedAt);
        }
        else
            Should.Throw<ArgumentException>(() => Prepare());
        source.Disposed.ShouldBeFalse();
    }

    private static RomOriginalMediaIdentification Ps2Facts(byte[] bytes)
    {
        var proof = new RomVerification(Sha256.FromSpan(SHA256.HashData(bytes)), bytes.Length, RomRepresentationEncoding.Identity,
            Sha256.FromSpan(SHA256.HashData(bytes)), bytes.Length, Sha1.FromSpan(SHA1.HashData(bytes)), Md5.FromSpan(MD5.HashData(bytes)),
            Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)), RomPs2BootObservation.MeasurementProfile,
            "decoder/test", "worker/test", new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        var boot = new RomPs2BootLayoutFacts(bytes.Length, 64, 16, 24, 200, 25, 60, 26, 512,
            SyntheticPs2Iso.BootPath, SyntheticPs2Iso.BootPath, SyntheticPs2Iso.BootSerial, SyntheticPs2Iso.EntryPoint, 1,
            RomPs2BootLayoutFacts.ProfileVersion, proof.DecoderBuild);
        var inspection = new RomLogicalInspection(Guid.NewGuid(), proof.Identity, "complete-original-observation/v1",
            proof.DecoderBuild, proof.VerifiedAt, bytes.AsSpan(0, 32), layout: boot);
        return new(proof, inspection, null, new(proof, boot));
    }

    private static RomPreparationService Ps2Service(RomOriginalMediaIdentification facts, ReferenceStore store)
    {
        var identifier = Substitute.For<IRomOriginalMediaIdentifier>();
        var admission = Substitute.For<IRomOriginalMediaIdentificationAdmission>();
        identifier.IdentifyAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(facts);
        identifier.AcquireAsync(Arg.Any<CancellationToken>()).Returns(admission);
        admission.IdentifyAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(facts);
        return new(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("Use the supplied completed measurement."),
            () => identifier,
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")),
            new RomPs2DvdEligibilityResolver(store));
    }

    private static RomDiscMediaReferenceRecord Reference(RomVerification proof, RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5) =>
        new(Guid.NewGuid(), RomDiscReferenceSystem.PlayStation2, media, 1, proof.ContentSize, proof.Sha1, proof.Md5, proof.Crc32,
            4096, Sha256.Parse(new string('c', 64)), "synthetic-official-record", "reference-parser/v1", "synthetic-local-import/v1", proof.VerifiedAt);

    private sealed class ReferenceStore(IReadOnlyList<RomDiscMediaReferenceRecord> records) : IRomDiscMediaReferenceStore
    {
        public ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>> Response { get; set; } = ErrorOrFactory.From(records);
        public Action? OnFind { get; init; }
        public Func<Task>? DuringFind { get; init; }
        public int Calls { get; private set; }
        public RomDiscMediaReferenceQuery? LastQuery { get; private set; }
        public int LastLimit { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public void SetRows(IReadOnlyList<RomDiscMediaReferenceRecord> rows) => Response = ErrorOrFactory.From(rows);
        public async Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>> FindAsync(RomDiscMediaReferenceQuery query, int maximumResults, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = query;
            LastLimit = maximumResults;
            LastToken = ct;
            OnFind?.Invoke();
            if (DuringFind is not null)
                await DuringFind();
            return Response;
        }
    }

    private sealed class PrefixOnlySource : ITempFile
    {
        private readonly byte[] _bytes;
        private readonly int _maximumOpens;
        public PrefixOnlySource(byte[] bytes, int maximumOpens = 1)
        {
            _bytes = bytes;
            _maximumOpens = maximumOpens;
            FileSha256 = Sha256.FromSpan(SHA256.HashData(bytes));
        }
        public string Path => "synthetic-original.iso";
        public Sha256 FileSha256 { get; }
        public long FileSize => _bytes.Length;
        public int OpenReads { get; private set; }
        public int ReadBytes { get; private set; }
        public bool Disposed { get; private set; }
        public Stream OpenRead()
        {
            if (++OpenReads > _maximumOpens)
                throw new InvalidOperationException("Resolution must not reread the original.");
            return new CountingPrefix(_bytes, count => ReadBytes += count);
        }
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private sealed class CountingPrefix(byte[] input, Action<int> read) : MemoryStream(input)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
            {
                if (Position + destination.Length > RomPreparationDetection.MaximumPrefixBytes)
                    throw new InvalidOperationException("Only the bounded routing prefix may be read by preparation.");
                int count = await base.ReadAsync(destination, ct);
                read(count);
                return count;
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_OrdinaryIsoAboveMediaLimitBelowUploadLimit_UsesRealIdentityPath()
    {
        string directory = Directory.CreateTempSubdirectory("romd-ordinary-iso-").FullName;
        byte[] bytes = Enumerable.Repeat((byte)0x5A, 4096).ToArray();
        try
        {
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            options.MaxUploadBytes.Returns(8192);
            var factory = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions
            {
                MaximumBytes = 8192,
                MaximumFiles = 1,
                MinimumFreeBytes = 0,
                AdmissionTimeoutSeconds = 1
            });
            using var verifier = new RvzVerifier(new() { MaxArtifactBytes = 1024 });
            var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());
            IRomPreparationAdmission? admission = null;
            await using var staged = await factory.CreateAsync(new MemoryStream(bytes),
                includeDatHashes: prefix => service.RequiresDatHashes(prefix, "ordinary.iso"),
                beforeCopy: async (prefix, ct) => admission = await service.AdmitAsync(prefix, "ordinary.iso", ct),
                selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "ordinary.iso"));

            admission.ShouldBeNull();
            staged.DatHashes!.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));

            var result = await service.PrepareAsync(staged, "ordinary.iso");

            result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Description : null);
            await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
            prepared.Hashes.BytesRead.ShouldBe(bytes.Length);
            prepared.Hashes.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
            prepared.Verification.Profile.ShouldBe("identity/v1");
            prepared.Eligibility.ShouldBeNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PrepareAsync_UnknownIsoWithOccupiedMediaSlot_UsesIdentityWithoutAdmission()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source([1, 2, 3]);
        using var verifier = fixture.Verifier();
        using var occupied = await verifier.AcquireAsync(CancellationToken.None);

        await using var prepared = (await MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()).PrepareAsync(source, "ordinary.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Verification.Profile.ShouldBe("identity/v1");
        prepared.Eligibility.ShouldBeNull();
        File.Exists(source.Path + ".pid").ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareAsync_UnknownIso_NeverResolvesVerifier()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source([1, 2, 3]);
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("Unknown content must not resolve media processing."),
            () => throw new InvalidOperationException("Unknown content must not resolve original identification."),
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));

        await using var prepared = (await service.PrepareAsync(source, "ordinary.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        prepared.Eligibility.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_RecognizedNintendoIso_StillEnforcesMediaLimits()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticNintendoIso.GameCube());
        using var verifier = new RvzVerifier(new() { MaxArtifactBytes = 1024 });

        var result = await MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()).PrepareAsync(source, "disc.iso");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.Media.LimitExceeded");
    }

    [Theory]
    [InlineData("disc.iso", true)]
    [InlineData("DISC.GCM", true)]
    [InlineData("disc.wii", true)]
    [InlineData("disc.bin", true)]
    public void RequiresDatHashes_UnknownContent_RoutesWithoutResolvingVerifier(string filename, bool expected)
    {
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => throw new InvalidOperationException("No inspection occurs while choosing an adapter."), new MediaInfrastructureTestServices.EmptyReferences());

        service.RequiresDatHashes(new byte[] { 1, 2, 3, 4 }, filename).ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_SupportedIso_BindsIdentityMeasurementAndIndependentEligibility(bool korean)
    {
        byte[] bytes = korean ? SyntheticNintendoIso.Wii(1) : SyntheticNintendoIso.GameCube();
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(bytes);
        using var verifier = fixture.Verifier();
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());

        await using var prepared = (await service.PrepareAsync(source, "disc.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        prepared.EncodingProfile.ShouldBe("identity/v1");
        prepared.LogicalFilename.ShouldBe("disc.iso");
        prepared.Artifact.ShouldBeSameAs(source);
        prepared.ContentSha256.ShouldBe(source.FileSha256);
        prepared.Hashes.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        prepared.Hashes.Md5.ShouldBe(Md5.FromSpan(MD5.HashData(bytes)));
        prepared.Hashes.Crc32.ShouldBe(Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
        prepared.Hashes.BytesRead.ShouldBe(bytes.Length);
        prepared.Verification.Profile.ShouldBe(RvzVerifier.IsoProfile);
        prepared.Verification.VerifierBuild.ShouldStartWith("romd-media-worker/mvid:");
        var eligibility = prepared.Eligibility.ShouldNotBeNull();
        eligibility.ContentSha256.ShouldBe(source.FileSha256);
        eligibility.ContentSize.ShouldBe(bytes.Length);
        eligibility.Adapter.ShouldBe("nintendo-iso");
        eligibility.Profile.ShouldBe(NintendoIsoInspector.ProfileVersion);
        eligibility.InspectorBuild.ShouldBe(prepared.Verification.DecoderBuild);
        eligibility.Status.ShouldBe(RomIsoEligibilityStatus.Supported);
        eligibility.DiscKind.ShouldBe(korean ? RomIsoDiscKind.Wii : RomIsoDiscKind.GameCube);
        eligibility.KoreanKeyPartitions.ShouldBe(korean ? 1 : 0);
        prepared.SourceEquivalence.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task PrepareAsync_UnknownTinyIso_PreservesFullOriginalWithoutInventedEligibility(int length)
    {
        byte[] bytes = Enumerable.Repeat((byte)0x5A, length).ToArray();
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(bytes);
        using var verifier = fixture.Verifier();
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());

        await using var prepared = (await service.PrepareAsync(source, "ordinary.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.ContentSha256.ShouldBe(source.FileSha256);
        prepared.Hashes.BytesRead.ShouldBe(length);
        prepared.Eligibility.ShouldBeNull();
        prepared.Verification.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
    }

    [Fact]
    public async Task PrepareAsync_UnsupportedCommonKeyIndex_PreservesOriginalWithoutLayoutClaim()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticNintendoIso.Wii(2));
        using var verifier = fixture.Verifier();

        await using var prepared = (await MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()).PrepareAsync(source, "disc.wii")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Eligibility!.Status.ShouldBe(RomIsoEligibilityStatus.Unsupported);
        prepared.Eligibility.DiscKind.ShouldBeNull();
        prepared.ContentSha256.ShouldBe(source.FileSha256);
    }

    [Theory]
    [InlineData("disc.GCM", false)]
    [InlineData("disc.wii", true)]
    public async Task PrepareAsync_EligibleNintendoCandidate_NormalizesOriginalDeliveryFilenameToIso(string filename, bool wii)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(wii ? SyntheticNintendoIso.Wii(0) : SyntheticNintendoIso.GameCube());
        using var verifier = fixture.Verifier();

        await using var prepared = (await MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()).PrepareAsync(source, filename)).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.LogicalFilename.ShouldBe("disc.iso");
    }

    [Theory]
    [InlineData("ordinary.GCM")]
    [InlineData("ordinary.wii")]
    public async Task PrepareAsync_UnknownCandidate_PreservesOriginalFilename(string filename)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source([1, 2, 3]);
        using var verifier = fixture.Verifier();

        await using var prepared = (await MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()).PrepareAsync(source, filename)).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.LogicalFilename.ShouldBe(filename);
    }

    [Fact]
    public async Task PrepareAsync_RvzMagicWithIsoFilename_RvzAdapterStillTakesPrecedence()
    {
        var disc = new SyntheticRvzBuilder(0x100000, 0x20000).Build();
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(disc.File);
        using var verifier = fixture.Verifier();

        await using var prepared = (await MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()).PrepareAsync(source, "disc.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        prepared.Verification.Profile.ShouldBe(RvzVerifier.Profile);
        prepared.Eligibility.ShouldBeNull();
    }

    [Fact]
    public async Task AdmitAsync_IsoReservationBeforeStaging_SharesCapacityWithRvzUntilInspectionCompletes()
    {
        using var fixture = new MediaWorkerTestFixture();
        using var verifier = fixture.Verifier();
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());
        using var admission = (await service.AdmitAsync(SyntheticNintendoIso.GameCube().AsMemory(0, 32), "disc.iso")).ShouldNotBeNull();
        var disc = new SyntheticRvzBuilder(0x100000, 0x20000).Build();
        await using var rvz = fixture.Source(disc.File, "rvz-sibling");

        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.VerifyAsync(rvz));
        File.Exists(rvz.Path + ".pid").ShouldBeFalse();
        await using var iso = fixture.Source(SyntheticNintendoIso.GameCube(), "iso-candidate");
        await using var prepared = (await admission.PrepareAsync(iso, "disc.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Eligibility!.Status.ShouldBe(RomIsoEligibilityStatus.Supported);
        (await verifier.VerifyAsync(rvz)).IsError.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => admission.PrepareAsync(iso, "disc.iso"));
    }

    [Theory]
    [InlineData(MediaWorkerFailure.Startup)]
    [InlineData(MediaWorkerFailure.Protocol)]
    [InlineData(MediaWorkerFailure.WallDeadline)]
    [InlineData(MediaWorkerFailure.CpuDeadline)]
    [InlineData(MediaWorkerFailure.Malformed)]
    [InlineData(MediaWorkerFailure.Unsupported)]
    [InlineData(MediaWorkerFailure.IoFailure)]
    public async Task PrepareAsync_ChildExecutionFailure_ProducesNoPreparedOrNegativeEligibilityClaim(MediaWorkerFailure failure)
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticNintendoIso.GameCube());
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        supervisor.InspectIsoAsync(source, Arg.Any<CancellationToken>()).Returns(MediaWorkerResult.Failed(failure));
        using var verifier = new RvzVerifier(fixture.Options, supervisor);

        var result = await MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()).PrepareAsync(source, "disc.iso");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.Media." + failure);
        await supervisor.Received(1).InspectIsoAsync(source, Arg.Any<CancellationToken>());
        using var recovered = await verifier.AcquireAsync(CancellationToken.None);
    }
}
