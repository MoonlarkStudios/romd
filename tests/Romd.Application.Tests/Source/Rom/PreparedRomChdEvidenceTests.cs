using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class PreparedRomChdEvidenceTests
{
    private const long LogicalBytes = 64 * 2048;
    private const string CapturedProfile = "dvd;unit=2048;hunk=16384;codec=zlib;processors=1";
    private const string Recipe = "chd-dvd-experiment/v1";
    private static readonly Sha256 Content = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 ArtifactHash = Sha256.Parse(new string('b', 64));
    private static readonly Sha256 OtherHash = Sha256.Parse(new string('e', 64));
    private static readonly Sha1 LogicalSha1 = Sha1.Parse(new string('c', 40));
    private static readonly Sha1 OtherSha1 = Sha1.Parse(new string('e', 40));
    private static readonly Sha1 OverallSha1 = Sha1.Parse(new string('f', 40));
    private static readonly Md5 LogicalMd5 = Md5.Parse(new string('d', 32));
    private static readonly Md5 OtherMd5 = Md5.Parse(new string('e', 32));
    private static readonly Crc32 LogicalCrc = Crc32.FromUInt32(123);
    private static readonly DateTimeOffset MeasuredAt = DateTimeOffset.UnixEpoch.AddTicks(1234);

    [Fact]
    public void Create_ImportedChdIntegrity_PreservesExactMeasurementWithoutInventedProvenance()
    {
        var output = Output();
        var integrity = Integrity(output);
        var artifact = new Artifact(output);

        var prepared = Create(output, integrity, artifact: artifact);

        prepared.Verification.ShouldBeSameAs(output);
        prepared.ChdIntegrity.ShouldBeSameAs(integrity);
        prepared.ChdIntegrity!.MeasurementVerifiedAt.ShouldBe(MeasuredAt);
        prepared.SourceEquivalence.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
        prepared.ChdPs2Boot.ShouldBeNull();
        prepared.Ps2BootEvidence.ShouldBeNull();
        prepared.Ps2Observation.ShouldBeNull();
        prepared.Eligibility.ShouldBeNull();
        prepared.EncodingProfile.ShouldBe(CapturedProfile);
        artifact.ReadCalls.ShouldBe(0);
    }

    [Fact]
    public void Create_ChdBootWithoutDvdReference_PreservesBootWithoutInventedEligibility()
    {
        var output = Output();
        var boot = Boot();
        var artifact = new Artifact(output);

        var prepared = Create(output, Integrity(output), boot: boot, artifact: artifact);

        prepared.ChdPs2Boot.ShouldBeSameAs(boot);
        prepared.Eligibility.ShouldBeNull();
        prepared.Ps2Observation.ShouldBeNull();
        prepared.SourceEquivalence.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
        artifact.ReadCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Create_PendingOriginalOrChdBoot_PreservesExactMeasurementAndIntegrityWithoutReferenceAuthority(
        bool chd, bool zeroCrc)
    {
        var measured = Output(zeroCrc);
        var boot = Boot();
        var proof = chd ? measured : OriginalPs2(measured, boot);
        var integrity = chd ? Integrity(proof) : null;
        var observation = chd ? null : new RomPs2BootObservation(proof, boot);
        var artifact = new Artifact(proof);

        var prepared = Create(proof, integrity, boot: chd ? boot : null,
            originalObservation: observation, artifact: artifact);

        var retained = prepared.Ps2BootEvidence.ShouldNotBeNull();
        retained.Id.ShouldBe(proof.Id);
        retained.Measurement.ShouldBeSameAs(proof);
        retained.MeasurementVerifiedAtUtcTicks.ShouldBe(MeasuredAt.UtcTicks);
        retained.Boot.ShouldBeSameAs(boot);
        retained.ChdIntegrity.ShouldBeSameAs(integrity);
        retained.ChdIntegrityId.ShouldBe(integrity?.Id);
        retained.MatchesMeasurement(proof).ShouldBeTrue();
        retained.Measurement.ContentSha256.ShouldBe(Content);
        retained.Measurement.ContentSize.ShouldBe(LogicalBytes);
        retained.Measurement.Sha1.ShouldBe(LogicalSha1);
        retained.Measurement.Md5.ShouldBe(LogicalMd5);
        retained.Measurement.Crc32.ShouldBe(zeroCrc ? Crc32.FromUInt32(0) : LogicalCrc);
        if (chd)
        {
            retained.ChdIntegrity!.Decoder.ShouldBeSameAs(integrity!.Decoder);
            retained.ChdIntegrity.Parameters.ShouldBeSameAs(integrity.Parameters);
            retained.ChdIntegrity.ObservedAt.ShouldBe(MeasuredAt.AddTicks(10));
            prepared.Ps2Observation.ShouldBeNull();
        }
        else
            prepared.Ps2Observation.ShouldBeSameAs(observation);
        prepared.Eligibility.ShouldBeNull();
        prepared.SourceEquivalence.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
        artifact.ReadCalls.ShouldBe(0);
        artifact.DisposeCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_ImportedChdSupportedPs2_BindsBootIndependentReferenceAndCompleteQuartet(bool zeroCrc)
    {
        var output = Output(zeroCrc);
        var boot = Boot();
        var eligibility = Eligibility(output, boot);

        var prepared = Create(output, Integrity(output), boot: boot, eligibility: eligibility);

        prepared.ChdPs2Boot.ShouldBeSameAs(boot);
        prepared.Eligibility.ShouldBeSameAs(eligibility);
        prepared.Eligibility!.Ps2DvdFacts!.DvdReference.ReferenceRecordId.ShouldBe(
            Guid.Parse("11111111-2222-3333-4444-555555555555"));
        prepared.Hashes.Crc32.ShouldBe(zeroCrc ? Crc32.FromUInt32(0) : LogicalCrc);
        prepared.SourceEquivalence.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_CreatedChd_BindsExactComparisonIntegrityOutputAndObservedParameters(bool zeroCrc)
    {
        var output = Output(zeroCrc);
        var integrity = Integrity(output);
        var comparison = Compare(output);
        var evidence = EncodingEvidence(output, integrity, comparison);
        var artifact = new Artifact(output);
        var boot = Boot();
        var eligibility = Eligibility(output, boot);

        var prepared = Create(output, integrity, comparison, evidence, boot, eligibility, artifact: artifact);

        prepared.SourceEquivalence.ShouldBeSameAs(comparison);
        prepared.ChdEncodingEvidence.ShouldBeSameAs(evidence);
        prepared.ChdEncodingEvidence!.SourceEquivalenceId.ShouldBe(comparison.Id);
        prepared.ChdEncodingEvidence.IntegrityId.ShouldBe(integrity.Id);
        prepared.ChdEncodingEvidence.OutputVerificationId.ShouldBe(output.Id);
        prepared.ChdEncodingEvidence.Parameters.ShouldBe(integrity.Parameters);
        prepared.ChdPs2Boot.ShouldBeSameAs(boot);
        prepared.Eligibility.ShouldBeSameAs(eligibility);
        prepared.Ps2Observation.ShouldBeNull();
        prepared.ChdEncodingEvidence.EncoderRecipe.ShouldBe(Recipe);
        prepared.EncodingProfile.ShouldBe(CapturedProfile);
        prepared.EncodingProfile.ShouldNotBe(prepared.ChdEncodingEvidence.EncoderRecipe);
        prepared.Hashes.Crc32.ShouldBe(zeroCrc ? Crc32.FromUInt32(0) : LogicalCrc);
        artifact.ReadCalls.ShouldBe(0);
    }

    [Fact]
    public void Create_ChdWithRetainedEarlierEligibility_PreservesIndependentObservationTime()
    {
        var output = Output();
        var boot = Boot();
        var earlier = Eligibility(output, boot, "earlier-inspection");
        earlier.InspectedAt.ShouldBeLessThan(output.VerifiedAt);

        var prepared = Create(output, Integrity(output), boot: boot, eligibility: earlier);

        prepared.Eligibility.ShouldBeSameAs(earlier);
        prepared.Eligibility!.InspectedAt.ShouldBe(MeasuredAt.AddDays(-1));
        prepared.ChdIntegrity!.MeasurementVerifiedAt.ShouldBe(output.VerifiedAt);
        prepared.ChdPs2Boot.ShouldBeSameAs(boot);
    }

    [Fact]
    public void Create_ChdWithoutIntegrity_RejectsBareMeasurement()
    {
        var output = Output();
        var artifact = new Artifact(output);

        Should.Throw<ArgumentException>(() => Create(output, null, artifact: artifact));

        artifact.ReadCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("verification-id")]
    [InlineData("artifact-hash")]
    [InlineData("artifact-size")]
    [InlineData("content-hash")]
    [InlineData("content-size")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("decoder")]
    [InlineData("measurement-profile")]
    [InlineData("verifier-build")]
    [InlineData("measurement-time")]
    [InlineData("captured-profile")]
    public void Create_RetainedIntegrityWithContradictoryMeasurement_RejectsExactTupleMismatch(string changed)
    {
        var output = Output();
        var original = Integrity(output);
        var retained = ChangeIntegrity(original, changed);
        var artifact = new Artifact(output);
        original.MatchesMeasurement(output).ShouldBeTrue();
        retained.MatchesMeasurement(output).ShouldBeFalse();
        if (changed != "verification-id")
            retained.VerificationId.ShouldBe(output.Id);

        Should.Throw<ArgumentException>(() => Create(output, retained, artifact: artifact));

        artifact.ReadCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_OnlyOneCreatedChdFact_RejectsUnpairedComparisonOrEncodingEvidence(bool comparisonOnly)
    {
        var output = Output();
        var integrity = Integrity(output);
        var comparison = Compare(output);
        var evidence = EncodingEvidence(output, integrity, comparison);

        Should.Throw<ArgumentException>(() => Create(output, integrity,
            comparisonOnly ? comparison : null, comparisonOnly ? null : evidence));
    }

    [Theory]
    [InlineData("comparison-id")]
    [InlineData("integrity-id")]
    [InlineData("output-id")]
    [InlineData("source-artifact")]
    [InlineData("source-size")]
    [InlineData("output-artifact")]
    [InlineData("output-size")]
    [InlineData("content-hash")]
    [InlineData("content-size")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("recipe")]
    [InlineData("encoder")]
    [InlineData("parameter-hunk")]
    [InlineData("parameter-codec")]
    [InlineData("parameter-profile")]
    public void Create_RetainedEncodingEvidenceWithContradictoryParents_RejectsExactBinding(string changed)
    {
        var output = Output();
        var integrity = Integrity(output);
        var comparison = Compare(output);
        var original = EncodingEvidence(output, integrity, comparison);
        var retained = ChangeEncodingEvidence(original, changed);
        var artifact = new Artifact(output);
        original.MatchesComparison(comparison).ShouldBeTrue();
        if (changed.StartsWith("parameter-", StringComparison.Ordinal))
        {
            retained.MatchesComparison(comparison).ShouldBeTrue("parameter mismatch must be caught separately from comparison");
            retained.Parameters.ShouldNotBe(integrity.Parameters);
        }
        else if (changed == "integrity-id")
        {
            retained.MatchesComparison(comparison).ShouldBeTrue("integrity ID must be caught separately from comparison");
            retained.IntegrityId.ShouldNotBe(integrity.Id);
        }
        else
            retained.MatchesComparison(comparison).ShouldBeFalse();

        Should.Throw<ArgumentException>(() => Create(output, integrity, comparison, retained, artifact: artifact));

        artifact.ReadCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(RomRepresentationEncoding.Identity, "integrity")]
    [InlineData(RomRepresentationEncoding.Identity, "encoding")]
    [InlineData(RomRepresentationEncoding.Identity, "boot")]
    [InlineData(RomRepresentationEncoding.Rvz, "integrity")]
    [InlineData(RomRepresentationEncoding.Rvz, "encoding")]
    [InlineData(RomRepresentationEncoding.Rvz, "boot")]
    public void Create_NonChdWithChdFacts_RejectsCrossEncodingEvidence(RomRepresentationEncoding encoding, string supplied)
    {
        var chd = Output();
        var integrity = Integrity(chd);
        var evidence = EncodingEvidence(chd, integrity, Compare(chd));
        var output = WholeOutput(encoding);

        Should.Throw<ArgumentException>(() => Create(output, supplied == "integrity" ? integrity : null,
            evidence: supplied == "encoding" ? evidence : null, boot: supplied == "boot" ? Boot() : null));
    }

    [Fact]
    public void Create_ChdBootOfDifferentLogicalSize_RejectsEvenWithoutEligibility()
    {
        var output = Output();
        var boot = Boot("logical-size");
        boot.LogicalBytes.ShouldNotBe(output.ContentSize);

        Should.Throw<ArgumentException>(() => Create(output, Integrity(output), boot: boot));
    }

    [Theory]
    [InlineData("missing-boot")]
    [InlineData("boot-layout")]
    [InlineData("boot-inspector")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("content-hash")]
    [InlineData("content-size")]
    public void Create_PositivePs2EligibilityWithForeignBootOrReference_RejectsLogicalMismatch(string changed)
    {
        var output = Output();
        var boot = Boot();
        var eligibility = Eligibility(output,
            changed is "boot-layout" or "boot-inspector" or "content-size" ? Boot(changed) : boot, changed);
        eligibility.Status.ShouldBe(RomIsoEligibilityStatus.Supported);
        eligibility.Ps2DvdFacts.ShouldNotBeNull();
        if (changed is "sha1" or "md5" or "crc32")
        {
            eligibility.ContentSha256.ShouldBe(output.ContentSha256);
            eligibility.ContentSize.ShouldBe(output.ContentSize);
        }

        Should.Throw<ArgumentException>(() => Create(output, Integrity(output),
            boot: changed == "missing-boot" ? null : boot, eligibility: eligibility));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_ChdCapturedProfileDiffersFromDeclaredMetadata_RejectsRelabeling(bool recipeAsProfile)
    {
        var output = Output();
        var integrity = Integrity(output);
        var comparison = Compare(output);
        var evidence = EncodingEvidence(output, integrity, comparison);

        Should.Throw<InvalidDataException>(() => Create(output, integrity, comparison, evidence,
            encodingProfile: recipeAsProfile ? Recipe : RomEncodingProfiles.UnknownImported));
    }

    [Fact]
    public void Create_ChdWithOriginalBootObservation_RejectsOriginalMeasurementReuse()
    {
        var output = Output();
        var boot = Boot();
        var original = OriginalPs2(output, boot);
        var observation = new RomPs2BootObservation(original, boot);

        Should.Throw<ArgumentException>(() => Create(output, Integrity(output), boot: boot, originalObservation: observation));
    }

    [Fact]
    public void WithPs2Eligibility_ChdBootFacts_DoNotBecomeBorrowedOriginalObservation()
    {
        var output = Output();
        var boot = Boot();
        var prepared = Create(output, Integrity(output), boot: boot);

        Should.Throw<InvalidOperationException>(() => prepared.WithPs2Eligibility(Eligibility(output, boot)));

        prepared.ChdPs2Boot.ShouldBeSameAs(boot);
        prepared.Ps2Observation.ShouldBeNull();
        prepared.Eligibility.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_ChdPreparation_PreservesBorrowedAndOwnedArtifactCustody(bool ownsArtifact)
    {
        var output = Output();
        var artifact = new Artifact(output);
        var prepared = Create(output, Integrity(output), ownsArtifact: ownsArtifact, artifact: artifact);

        await prepared.DisposeAsync();

        artifact.DisposeCalls.ShouldBe(ownsArtifact ? 1 : 0);
        artifact.ReadCalls.ShouldBe(0);
    }

    [Fact]
    public async Task WithPs2Eligibility_BorrowedOriginal_PreservesExistingMeasurementAndDisposal()
    {
        var output = Output();
        var boot = Boot();
        var original = OriginalPs2(output, boot);
        var artifact = new Artifact(original);
        var observation = new RomPs2BootObservation(original, boot);
        var prepared = Create(original, null, originalObservation: observation, artifact: artifact);
        var eligibility = Eligibility(original, boot);

        var resolved = prepared.WithPs2Eligibility(eligibility);
        await resolved.DisposeAsync();
        await prepared.DisposeAsync();

        resolved.Artifact.ShouldBeSameAs(artifact);
        resolved.Verification.ShouldBeSameAs(original);
        resolved.Ps2Observation.ShouldBeSameAs(observation);
        resolved.Ps2BootEvidence.ShouldBe(prepared.Ps2BootEvidence);
        resolved.Ps2BootEvidence!.Measurement.ShouldBeSameAs(original);
        resolved.Ps2BootEvidence.Boot.ShouldBeSameAs(boot);
        resolved.Ps2BootEvidence.MeasurementVerifiedAtUtcTicks.ShouldBe(original.VerifiedAt.UtcTicks);
        resolved.Ps2BootEvidence.ChdIntegrity.ShouldBeNull();
        resolved.Eligibility.ShouldBeSameAs(eligibility);
        resolved.ChdIntegrity.ShouldBeNull();
        resolved.ChdEncodingEvidence.ShouldBeNull();
        resolved.ChdPs2Boot.ShouldBeNull();
        artifact.DisposeCalls.ShouldBe(0);
        artifact.ReadCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_WholeFileRvzWithoutChdFacts_PreservesOptionalComparisonAndUnknownProfile(bool created)
    {
        var output = WholeOutput(RomRepresentationEncoding.Rvz);
        var comparison = created ? Compare(output, "rvz-canonical/v1") : null;

        var prepared = Create(output, null, comparison);

        prepared.SourceEquivalence.ShouldBeSameAs(comparison);
        prepared.EncodingProfile.ShouldBe(RomEncodingProfiles.UnknownImported);
        prepared.Verification.CapturedEncodingProfile.ShouldBeNull();
        prepared.ChdIntegrity.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
        prepared.ChdPs2Boot.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WithChdPs2Eligibility_BorrowedImportedAndCreatedChd_PreservesEveryFactAndOwnership(bool created, bool supported)
    {
        var output = Output();
        var integrity = Integrity(output);
        var comparison = created ? Compare(output) : null;
        var encoding = created ? EncodingEvidence(output, integrity, comparison!) : null;
        var boot = Boot();
        var artifact = new Artifact(output);
        var earlier = Eligibility(output, boot, "earlier-inspection");
        var prepared = Create(output, integrity, comparison, encoding, boot, earlier, artifact: artifact);
        var eligibility = supported ? Eligibility(output, boot) : null;

        var resolved = prepared.WithChdPs2Eligibility(eligibility);
        await resolved.DisposeAsync();
        await prepared.DisposeAsync();

        resolved.Artifact.ShouldBeSameAs(artifact);
        resolved.Verification.ShouldBeSameAs(output);
        resolved.ChdIntegrity.ShouldBeSameAs(integrity);
        resolved.SourceEquivalence.ShouldBeSameAs(comparison);
        resolved.ChdEncodingEvidence.ShouldBeSameAs(encoding);
        resolved.ChdPs2Boot.ShouldBeSameAs(boot);
        resolved.Ps2BootEvidence.ShouldBe(prepared.Ps2BootEvidence);
        resolved.Ps2BootEvidence!.Measurement.ShouldBeSameAs(output);
        resolved.Ps2BootEvidence.Boot.ShouldBeSameAs(boot);
        resolved.Ps2BootEvidence.ChdIntegrity.ShouldBeSameAs(integrity);
        resolved.Ps2BootEvidence.MeasurementVerifiedAtUtcTicks.ShouldBe(output.VerifiedAt.UtcTicks);
        resolved.Hashes.ShouldBeSameAs(prepared.Hashes);
        resolved.ContentSha256.ShouldBe(prepared.ContentSha256);
        resolved.LogicalFilename.ShouldBe(prepared.LogicalFilename);
        resolved.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        resolved.EncodingProfile.ShouldBe(CapturedProfile);
        resolved.Eligibility.ShouldBeSameAs(eligibility);
        resolved.Ps2Observation.ShouldBeNull();
        prepared.Eligibility.ShouldBeSameAs(earlier);
        prepared.Eligibility!.InspectedAt.ShouldBeLessThan(output.VerifiedAt);
        artifact.DisposeCalls.ShouldBe(0);
        artifact.ReadCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("missing-boot")]
    [InlineData("non-chd")]
    public void WithChdPs2Eligibility_IneligibleWrapper_RejectsWithoutReadingOrDisposing(string reason)
    {
        var output = reason == "non-chd" ? WholeOutput(RomRepresentationEncoding.Rvz) : Output();
        var artifact = new Artifact(output);
        var prepared = Create(output, reason == "non-chd" ? null : Integrity(output),
            boot: reason is "non-chd" or "missing-boot" ? null : Boot(), ownsArtifact: reason == "owned", artifact: artifact);

        Should.Throw<InvalidOperationException>(() => prepared.WithChdPs2Eligibility(null));

        artifact.ReadCalls.ShouldBe(0);
        artifact.DisposeCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("boot-layout")]
    [InlineData("sha1")]
    [InlineData("content-hash")]
    public void WithChdPs2Eligibility_ForeignPositiveReference_RejectsWithoutChangingRetainedFacts(string changed)
    {
        var output = Output();
        var boot = Boot();
        var integrity = Integrity(output);
        var prepared = Create(output, integrity, boot: boot);
        var foreign = Eligibility(output, changed == "boot-layout" ? Boot(changed) : boot, changed);

        Should.Throw<ArgumentException>(() => prepared.WithChdPs2Eligibility(foreign));

        prepared.ChdIntegrity.ShouldBeSameAs(integrity);
        prepared.ChdPs2Boot.ShouldBeSameAs(boot);
        prepared.Eligibility.ShouldBeNull();
    }

    [Theory]
    [InlineData(RomIsoDiscKind.GameCube, RomIsoEligibilityStatus.Supported)]
    [InlineData(RomIsoDiscKind.Wii, RomIsoEligibilityStatus.Supported)]
    [InlineData(null, RomIsoEligibilityStatus.Unsupported)]
    public void WithChdPs2Eligibility_UnrelatedOrUnsupportedObservation_RejectsWithoutReplacingPendingFacts(RomIsoDiscKind? kind, RomIsoEligibilityStatus status)
    {
        var output = Output();
        var artifact = new Artifact(output);
        var boot = Boot();
        var integrity = Integrity(output);
        var prepared = Create(output, integrity, boot: boot, artifact: artifact);
        var unrelated = new RomIsoEligibility(output.ContentSha256, output.ContentSize, "synthetic-iso", "eligibility/v1",
            "inspector/test", status, kind, kind == RomIsoDiscKind.Wii ? 1 : 0, kind == RomIsoDiscKind.Wii ? 1 : 0, 0,
            output.VerifiedAt);

        Should.Throw<ArgumentException>(() => prepared.WithChdPs2Eligibility(unrelated)).ParamName.ShouldBe("resolved");

        prepared.ChdIntegrity.ShouldBeSameAs(integrity);
        prepared.ChdPs2Boot.ShouldBeSameAs(boot);
        prepared.Eligibility.ShouldBeNull();
        artifact.ReadCalls.ShouldBe(0);
        artifact.DisposeCalls.ShouldBe(0);
    }

    private static PreparedRom.WholeFile Create(RomVerification output, RomChdIntegrity? integrity,
        RomSourceEquivalence? comparison = null, RomChdEncodingEvidence? evidence = null,
        RomPs2BootLayoutFacts? boot = null, RomIsoEligibility? eligibility = null, bool ownsArtifact = false,
        string? encodingProfile = null, Artifact? artifact = null, RomPs2BootObservation? originalObservation = null) =>
        new(artifact ?? new Artifact(output), "disc.iso", output.ContentSha256,
            new DatHashResult(output.Sha1, output.Md5, output.Crc32, output.ContentSize), output.Encoding, output, CapturedContainer(output),
            ownsArtifact: ownsArtifact, encodingProfile: encodingProfile ?? output.CapturedEncodingProfile,
            eligibility: eligibility, sourceEquivalence: comparison, ps2Observation: originalObservation,
            chdIntegrity: integrity, chdEncodingEvidence: evidence, chdPs2Boot: boot);

    private static RomContainerFacts CapturedContainer(RomVerification proof) => proof.Encoding switch
    {
        RomRepresentationEncoding.Identity => RomContainerFacts.Identity.Instance,
        RomRepresentationEncoding.Rvz => new RomContainerFacts.Rvz(0x01000000, 1, 5, 12, 131072, false, proof.ContentSize),
        _ => new RomContainerFacts.Chd(5, LogicalBytes, 16384, 2048, 8, [0x7a6c6962, 0, 0, 0], false, LogicalSha1, OverallSha1, [new(0x44564420, 1, new byte[] { 0 })])
    };

    private static RomVerification Output(bool zeroCrc = false) => new(ArtifactHash, 4096, RomRepresentationEncoding.Chd,
        Content, LogicalBytes, LogicalSha1, LogicalMd5, zeroCrc ? Crc32.FromUInt32(0) : LogicalCrc,
        "chd-import/v1", Decoder().BuildLabel, "verifier/mvid:synthetic", MeasuredAt, CapturedProfile);

    private static RomVerification WholeOutput(RomRepresentationEncoding encoding) => new(
        encoding == RomRepresentationEncoding.Identity ? Content : ArtifactHash,
        encoding == RomRepresentationEncoding.Identity ? LogicalBytes : 4096,
        encoding, Content, LogicalBytes, LogicalSha1, LogicalMd5, LogicalCrc,
        "legacy-output/v1", "legacy-decoder", "legacy-verifier", MeasuredAt);

    private static RomVerification Source(RomVerification output) => new(output.ContentSha256, output.ContentSize,
        RomRepresentationEncoding.Identity, output.ContentSha256, output.ContentSize, output.Sha1, output.Md5, output.Crc32,
        "identity-output/v1", "hasher", "verifier", MeasuredAt.AddTicks(-1));

    private static RomVerification OriginalPs2(RomVerification output, RomPs2BootLayoutFacts boot) => new(
        output.ContentSha256, output.ContentSize, RomRepresentationEncoding.Identity,
        output.ContentSha256, output.ContentSize, output.Sha1, output.Md5, output.Crc32,
        RomPs2BootObservation.MeasurementProfile, boot.InspectorBuild, "verifier/mvid:synthetic", MeasuredAt);

    private static RomChdContainerParameters Parameters(string? changed = null) => new(5,
        changed == "content-size" ? LogicalBytes + 2048 : LogicalBytes,
        changed == "parameter-hunk" ? 8192U : 16384U, 2048,
        changed == "content-size" ? 9U : changed == "parameter-hunk" ? 16U : 8U,
        changed == "parameter-codec" ? 0x6c7a6d61U : 0x7a6c6962U, 0, 0, 0, false,
        new RomChdDvdMetadataFacts(1, 0x44564420, 1, 1, 0,
            RomChdDvdMetadataFacts.ProfileVersion, RomChdDvdMetadataFacts.SingletonFingerprint),
        changed is "captured-profile" or "parameter-profile" ? "observed-other-header" : CapturedProfile);

    private static RomChdDecoderBuild Decoder(bool changed = false) => new("linux-arm64", OtherSha1, changed ? 2 : 1);

    private static RomChdEncoderBuild Encoder(bool changed = false) => new("linux-arm64", "mame0289",
        changed ? OtherHash : ArtifactHash, 1);

    private static RomChdIntegrity Integrity(RomVerification output)
    {
        RomChdIntegrity.TryCreate(output, Parameters(), output.Sha1, output.Sha1, OverallSha1, OverallSha1,
            Decoder(), RomChdIntegrity.ProfileVersion, MeasuredAt.AddTicks(10), out var integrity).ShouldBeTrue();
        return integrity.ShouldNotBeNull();
    }

    private static RomSourceEquivalence Compare(RomVerification output, string recipe = Recipe)
    {
        RomSourceEquivalence.TryCreate(Source(output), output, recipe,
            output.Encoding == RomRepresentationEncoding.Chd ? Encoder().BuildLabel : "legacy-encoder/mvid:synthetic",
            MeasuredAt.AddTicks(20), out var comparison).ShouldBeTrue();
        return comparison.ShouldNotBeNull();
    }

    private static RomChdEncodingEvidence EncodingEvidence(RomVerification output, RomChdIntegrity integrity,
        RomSourceEquivalence comparison)
    {
        RomChdEncodingEvidence.TryCreate(comparison, output, integrity, RecipeId.Parse(Recipe), Encoder(),
            MeasuredAt.AddTicks(30), out var evidence).ShouldBeTrue();
        return evidence.ShouldNotBeNull();
    }

    private static RomChdIntegrity ChangeIntegrity(RomChdIntegrity fact, string changed)
    {
        Sha1 sha1 = changed == "sha1" ? OtherSha1 : fact.Sha1;
        return RomChdIntegrity.Rehydrate(fact.Id, changed == "verification-id" ? Guid.NewGuid() : fact.VerificationId,
            changed == "artifact-hash" ? OtherHash : fact.ArtifactSha256,
            changed == "artifact-size" ? fact.ArtifactSize + 1 : fact.ArtifactSize,
            changed == "content-hash" ? OtherHash : fact.ContentSha256,
            changed == "content-size" ? fact.ContentSize + 2048 : fact.ContentSize, sha1,
            changed == "md5" ? OtherMd5 : fact.Md5, changed == "crc32" ? Crc32.FromUInt32(124) : fact.Crc32,
            Parameters(changed), sha1, sha1, fact.StoredOverallSha1, fact.ComputedOverallSha1,
            changed == "decoder" ? Decoder(true) : fact.Decoder,
            changed == "measurement-profile" ? "other-measurement/v1" : fact.MeasurementProfile,
            changed == "verifier-build" ? "other-verifier" : fact.VerifierBuild,
            changed == "measurement-time" ? fact.MeasurementVerifiedAt.AddTicks(1) : fact.MeasurementVerifiedAt,
            fact.Profile, fact.ObservedAt);
    }

    private static RomChdEncodingEvidence ChangeEncodingEvidence(RomChdEncodingEvidence fact, string changed) =>
        RomChdEncodingEvidence.Rehydrate(
            changed == "comparison-id" ? Guid.NewGuid() : fact.SourceEquivalenceId,
            changed == "integrity-id" ? Guid.NewGuid() : fact.IntegrityId,
            changed == "output-id" ? Guid.NewGuid() : fact.OutputVerificationId,
            changed == "source-artifact" ? OtherHash : fact.SourceArtifactSha256,
            changed == "source-size" ? fact.SourceArtifactSize + 1 : fact.SourceArtifactSize,
            changed == "output-artifact" ? OtherHash : fact.OutputArtifactSha256,
            changed == "output-size" ? fact.OutputArtifactSize + 1 : fact.OutputArtifactSize,
            changed == "content-hash" ? OtherHash : fact.ContentSha256,
            changed == "content-size" ? fact.ContentSize + 2048 : fact.ContentSize,
            changed == "sha1" ? OtherSha1 : fact.Sha1, changed == "md5" ? OtherMd5 : fact.Md5,
            changed == "crc32" ? Crc32.FromUInt32(124) : fact.Crc32,
            changed == "recipe" ? RecipeId.Parse("other-recipe/v1") : fact.Recipe,
            changed == "encoder" ? Encoder(true) : fact.Encoder, Parameters(changed), fact.EncodedAt);

    private static RomPs2BootLayoutFacts Boot(string? changed = null) => new(
        changed is "logical-size" or "content-size" ? LogicalBytes + 2048 : LogicalBytes,
        64, 16, 24, 2048, 25, 60, changed == "boot-layout" ? 27U : 26U, 512,
        "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x00100000, 1,
        RomPs2BootLayoutFacts.ProfileVersion, changed == "boot-inspector" ? "other-ps2-inspector" : "ps2-inspector/mvid:synthetic");

    private static RomIsoEligibility Eligibility(RomVerification output, RomPs2BootLayoutFacts boot, string? changed = null)
    {
        var reference = new RomDvdMediaReference(Guid.Parse("11111111-2222-3333-4444-555555555555"),
            changed == "content-hash" ? OtherHash : output.ContentSha256,
            changed == "content-size" ? output.ContentSize + 2048 : output.ContentSize,
            changed == "sha1" ? OtherSha1 : output.Sha1, changed == "md5" ? OtherMd5 : output.Md5,
            changed == "crc32" ? Crc32.FromUInt32(124) : output.Crc32,
            RomDvdMediaKind.Dvd5, "dvd-reference/v1", OtherHash, "synthetic-independent-reference");
        var details = new RomPs2DvdEligibilityFacts(reference.ContentSha256, reference.ContentSize,
            reference.Sha1, reference.Md5, reference.Crc32, boot, reference);
        return new RomIsoEligibility(details.ContentSha256, details.ContentSize, "ps2-iso",
            RomPs2DvdEligibilityFacts.ProfileVersion, boot.InspectorBuild, RomIsoEligibilityStatus.Supported,
            RomIsoDiscKind.PlayStation2Dvd, 0, 0, 0,
            changed == "earlier-inspection" ? output.VerifiedAt.AddDays(-1) : output.VerifiedAt, details);
    }

    private sealed class Artifact(RomVerification verification) : ITempFile
    {
        public int ReadCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public string Path => "unused-constructor-fixture";
        public Sha256 FileSha256 => verification.ArtifactSha256;
        public long FileSize => verification.ArtifactSize;
        public Stream OpenRead()
        {
            ReadCalls++;
            throw new InvalidOperationException("Constructor validation must not read payload bytes.");
        }
        public bool TryBeginChildUse() => throw new InvalidOperationException("Constructor validation does not launch children.");
        public bool TryEndChildUse() => throw new InvalidOperationException("Constructor validation does not launch children.");
        public bool Quarantine() => throw new InvalidOperationException("Constructor validation does not quarantine artifacts.");
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
