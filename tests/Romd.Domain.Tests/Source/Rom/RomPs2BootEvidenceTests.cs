using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomPs2BootEvidenceTests
{
    private static readonly DateTimeOffset MeasuredAt = new DateTimeOffset(2026, 10, 10, 7, 8, 9, TimeSpan.FromHours(-5)).AddTicks(1234567);
    private static readonly DateTimeOffset IntegrityObservedAt = MeasuredAt.AddSeconds(1).AddTicks(3);

    [Fact]
    public void FromOriginal_ValidatedZeroCrcObservation_RetainsFactsWithoutReferenceAuthority()
    {
        var observation = new RomPs2BootObservation(Original(), ChdEvidenceSamples.Boot());

        var evidence = RomPs2BootEvidence.FromOriginal(observation);

        evidence.Id.ShouldBe(observation.Measurement.Id);
        evidence.Measurement.ShouldBeSameAs(observation.Measurement);
        evidence.Boot.ShouldBeSameAs(observation.Boot);
        evidence.Measurement.Crc32.IsEmpty.ShouldBeFalse();
        evidence.Measurement.Crc32.ShouldBe(Crc32.FromUInt32(0));
        evidence.MeasurementVerifiedAtUtcTicks.ShouldBe(MeasuredAt.UtcTicks);
        evidence.ChdIntegrity.ShouldBeNull();
        evidence.ChdIntegrityId.ShouldBeNull();
        evidence.MatchesMeasurement(observation.Measurement).ShouldBeTrue();
        evidence.MatchesMeasurement(Original()).ShouldBeFalse();
        evidence.MatchesMeasurement(null).ShouldBeFalse();
    }

    [Fact]
    public void FromChd_ExactZeroCrcIntegrity_RetainsNativeAndDistinctManagedInspectorIdentities()
    {
        var measured = ChdEvidenceSamples.Output("zero-crc", verifiedAt: MeasuredAt);
        var integrity = ChdEvidenceSamples.Integrity(measured, IntegrityObservedAt);
        var boot = ChdEvidenceSamples.Boot();
        boot.InspectorBuild.ShouldNotBe(measured.DecoderBuild);
        boot.InspectorBuild.ShouldNotBe(measured.VerifierBuild);

        var evidence = RomPs2BootEvidence.FromChd(measured, boot, integrity);

        evidence.Id.ShouldBe(measured.Id);
        evidence.Measurement.ShouldBeSameAs(measured);
        evidence.Boot.ShouldBeSameAs(boot);
        evidence.ChdIntegrity.ShouldBeSameAs(integrity);
        evidence.ChdIntegrityId.ShouldBe(integrity.Id);
        integrity.MatchesMeasurement(evidence.Measurement).ShouldBeTrue();
        evidence.Measurement.Crc32.IsEmpty.ShouldBeFalse();
        evidence.Measurement.Crc32.ShouldBe(Crc32.FromUInt32(0));
        evidence.MeasurementVerifiedAtUtcTicks.ShouldBe(MeasuredAt.UtcTicks);
        integrity.MeasurementVerifiedAt.UtcTicks.ShouldBe(MeasuredAt.UtcTicks);
        integrity.ObservedAt.UtcTicks.ShouldBe(IntegrityObservedAt.UtcTicks);
        integrity.Decoder.ShouldBe(ChdEvidenceSamples.Decoder());
        Should.Throw<ArgumentException>(() => new RomPs2BootObservation(measured, boot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rehydrate_ExactRetainedFacts_PreservesKeysQuartetAndSubMillisecondTime(bool chd)
    {
        var evidence = Evidence(chd);
        var restoredMeasurement = RestoreMeasurement(evidence.Measurement);
        var restoredIntegrity = evidence.ChdIntegrity is { } integrity ? RestoreIntegrity(integrity) : null;

        var restored = RomPs2BootEvidence.Rehydrate(evidence.Id, restoredMeasurement, evidence.Boot,
            evidence.MeasurementVerifiedAtUtcTicks, evidence.ChdIntegrityId, restoredIntegrity);

        restored.ShouldBe(evidence);
        restored.Measurement.ShouldNotBeSameAs(evidence.Measurement);
        restored.Id.ShouldBe(evidence.Id);
        restored.Measurement.Id.ShouldBe(evidence.Id);
        restored.Measurement.Sha1.ShouldBe(evidence.Measurement.Sha1);
        restored.Measurement.Md5.ShouldBe(evidence.Measurement.Md5);
        restored.Measurement.Crc32.ShouldBe(evidence.Measurement.Crc32);
        restored.Measurement.ContentSize.ShouldBe(evidence.Measurement.ContentSize);
        restored.MeasurementVerifiedAtUtcTicks.ShouldBe(MeasuredAt.UtcTicks);
        (restored.MeasurementVerifiedAtUtcTicks % TimeSpan.TicksPerMillisecond).ShouldNotBe(0L);
        restored.ChdIntegrityId.ShouldBe(evidence.ChdIntegrityId);
        restored.MatchesMeasurement(restoredMeasurement).ShouldBeTrue();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("rounded")]
    [InlineData("one-tick")]
    [InlineData("negative")]
    [InlineData("beyond-clock")]
    public void Rehydrate_AbsentOrChangedExactOriginalTicks_RefusesCachedEvidence(string changed)
    {
        var evidence = Evidence(false);
        long? ticks = changed switch
        {
            "null" => null,
            "rounded" => evidence.MeasurementVerifiedAtUtcTicks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
            "one-tick" => evidence.MeasurementVerifiedAtUtcTicks + 1,
            "negative" => -1,
            "beyond-clock" => DateTimeOffset.MaxValue.UtcTicks + 1,
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };

        Should.Throw<ArgumentException>(() => RomPs2BootEvidence.Rehydrate(evidence.Id, evidence.Measurement, evidence.Boot, ticks, null, null));
    }

    [Fact]
    public void Rehydrate_LegacyMillisecondOnlyOriginal_RefusesMissingPrecision()
    {
        var measured = Original();
        var rounded = new DateTimeOffset(measured.VerifiedAt.UtcTicks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var legacyMeasurement = RestoreMeasurement(measured, time: rounded);

        Should.Throw<ArgumentException>(() => RomPs2BootEvidence.Rehydrate(legacyMeasurement.Id, legacyMeasurement,
            ChdEvidenceSamples.Boot(), null, null, null));
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("wrong-id")]
    [InlineData("missing-integrity-id")]
    [InlineData("empty-integrity-id")]
    [InlineData("wrong-integrity-id")]
    [InlineData("missing-integrity")]
    [InlineData("different-integrity")]
    public void Rehydrate_ChangedChdKeysOrMissingIntegrity_Rejects(string changed)
    {
        var evidence = Evidence(true);
        var id = changed == "empty-id" ? Guid.Empty : changed == "wrong-id" ? Guid.NewGuid() : evidence.Id;
        Guid? integrityId = changed == "missing-integrity-id" ? null : changed == "empty-integrity-id" ? Guid.Empty :
            changed == "wrong-integrity-id" ? Guid.NewGuid() : evidence.ChdIntegrityId;
        var integrity = changed == "missing-integrity" ? null : changed == "different-integrity" ?
            Evidence(true).ChdIntegrity : evidence.ChdIntegrity;

        Should.Throw<ArgumentException>(() => RomPs2BootEvidence.Rehydrate(id, evidence.Measurement, evidence.Boot,
            evidence.MeasurementVerifiedAtUtcTicks, integrityId, integrity));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("artifact-hash")]
    [InlineData("artifact-length")]
    [InlineData("content-hash")]
    [InlineData("content-length")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc")]
    [InlineData("empty-crc")]
    [InlineData("encoding")]
    [InlineData("profile")]
    [InlineData("decoder")]
    [InlineData("verifier")]
    [InlineData("time")]
    [InlineData("captured-profile")]
    public void FromChd_AlteredMeasurement_RejectsExactIntegrityJoin(string changed)
    {
        var evidence = Evidence(true);
        var changedMeasurement = RestoreMeasurement(evidence.Measurement, changed: changed);

        Should.Throw<ArgumentException>(() => RomPs2BootEvidence.FromChd(changedMeasurement, evidence.Boot, evidence.ChdIntegrity!));
    }

    [Fact]
    public void FromChd_ValidBootForDifferentLogicalLength_Rejects()
    {
        var evidence = Evidence(true);

        Should.Throw<ArgumentException>(() => RomPs2BootEvidence.FromChd(evidence.Measurement,
            Boot(logicalBytes: ChdEvidenceSamples.LogicalBytes + 2048), evidence.ChdIntegrity!));
    }

    [Theory]
    [InlineData("integrity-id-only")]
    [InlineData("integrity-only")]
    [InlineData("integrity-both")]
    [InlineData("wrong-profile")]
    [InlineData("wrong-inspector")]
    [InlineData("absent-crc")]
    [InlineData("rvz")]
    public void Rehydrate_OriginalWithBorrowedOrInvalidFacts_Rejects(string changed)
    {
        var evidence = Evidence(false);
        var chdIntegrity = Evidence(true).ChdIntegrity!;
        var measurement = RestoreMeasurement(evidence.Measurement, changed: changed switch
        {
            "wrong-profile" => "profile",
            "wrong-inspector" => "decoder",
            "absent-crc" => "empty-crc",
            "rvz" => "encoding",
            _ => null
        });
        var integrityId = changed is "integrity-id-only" or "integrity-both" ? chdIntegrity.Id : (Guid?)null;
        var integrity = changed is "integrity-only" or "integrity-both" ? chdIntegrity : null;

        Should.Throw<ArgumentException>(() => RomPs2BootEvidence.Rehydrate(measurement.Id, measurement, evidence.Boot,
            measurement.VerifiedAt.UtcTicks, integrityId, integrity));
    }

    [Fact]
    public void Rehydrate_ChangedValidBootOrNativeProvenance_ProducesDistinctImmutableFacts()
    {
        var evidence = Evidence(true);
        var changedBoot = RomPs2BootEvidence.FromChd(evidence.Measurement, Boot(entryPoint: evidence.Boot.EntryPoint + 4), evidence.ChdIntegrity!);
        var decoder = evidence.ChdIntegrity!.Decoder;
        var changedDecoder = new RomChdDecoderBuild(decoder.Rid, decoder.UpstreamCommit, decoder.RecipeVersion + 1);
        var changedIntegrity = RestoreIntegrity(evidence.ChdIntegrity!, changedDecoder);

        changedBoot.Id.ShouldBe(evidence.Id);
        changedBoot.ShouldNotBe(evidence);
        // The label carries the whole persisted decoder tuple, so changed provenance no longer binds the measurement.
        changedDecoder.BuildLabel.ShouldNotBe(decoder.BuildLabel);
        changedIntegrity.MatchesMeasurement(evidence.Measurement).ShouldBeFalse();
    }

    [Fact]
    public void CreateOrRehydrate_NullFacts_Rejects()
    {
        var evidence = Evidence(true);

        Should.Throw<ArgumentNullException>(() => RomPs2BootEvidence.FromOriginal(null!));
        Should.Throw<ArgumentNullException>(() => RomPs2BootEvidence.FromChd(null!, evidence.Boot, evidence.ChdIntegrity!));
        Should.Throw<ArgumentNullException>(() => RomPs2BootEvidence.FromChd(evidence.Measurement, null!, evidence.ChdIntegrity!));
        Should.Throw<ArgumentNullException>(() => RomPs2BootEvidence.FromChd(evidence.Measurement, evidence.Boot, null!));
        Should.Throw<ArgumentNullException>(() => RomPs2BootEvidence.Rehydrate(evidence.Id, null!, evidence.Boot,
            evidence.MeasurementVerifiedAtUtcTicks, evidence.ChdIntegrityId, evidence.ChdIntegrity));
        Should.Throw<ArgumentNullException>(() => RomPs2BootEvidence.Rehydrate(evidence.Id, evidence.Measurement, null!,
            evidence.MeasurementVerifiedAtUtcTicks, evidence.ChdIntegrityId, evidence.ChdIntegrity));
    }

    private static RomVerification Original() => new(ChdEvidenceSamples.Content, ChdEvidenceSamples.LogicalBytes,
        RomRepresentationEncoding.Identity, ChdEvidenceSamples.Content, ChdEvidenceSamples.LogicalBytes,
        ChdEvidenceSamples.LogicalSha1, ChdEvidenceSamples.LogicalMd5, Crc32.FromUInt32(0),
        RomPs2BootObservation.MeasurementProfile, ChdEvidenceSamples.Boot().InspectorBuild, "worker/mvid:synthetic",
        MeasuredAt, RomEncodingProfiles.Identity);

    private static RomPs2BootEvidence Evidence(bool chd)
    {
        if (!chd)
            return RomPs2BootEvidence.FromOriginal(new RomPs2BootObservation(Original(), ChdEvidenceSamples.Boot()));
        var measured = ChdEvidenceSamples.Output(verifiedAt: MeasuredAt);
        return RomPs2BootEvidence.FromChd(measured, ChdEvidenceSamples.Boot(), ChdEvidenceSamples.Integrity(measured, IntegrityObservedAt));
    }

    private static RomPs2BootLayoutFacts Boot(long logicalBytes = ChdEvidenceSamples.LogicalBytes, uint entryPoint = 0x00100000) =>
        new(logicalBytes, 64, 16, 24, 2048, 25, 60, 26, 512, "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1",
            "SLUS_203.12", entryPoint, 1, RomPs2BootLayoutFacts.ProfileVersion, "ps2-inspector/mvid:synthetic");

    private static RomVerification RestoreMeasurement(RomVerification measurement, string? changed = null, DateTimeOffset? time = null) =>
        RomVerification.Rehydrate(changed == "id" ? Guid.NewGuid() : measurement.Id,
            changed == "artifact-hash" ? ChdEvidenceSamples.OtherHash : measurement.ArtifactSha256,
            changed == "artifact-length" ? measurement.ArtifactSize + 1 : measurement.ArtifactSize,
            changed == "encoding" ? RomRepresentationEncoding.Rvz : measurement.Encoding,
            changed == "content-hash" ? ChdEvidenceSamples.OtherHash : measurement.ContentSha256,
            changed == "content-length" ? measurement.ContentSize + 2048 : measurement.ContentSize,
            changed == "sha1" ? ChdEvidenceSamples.OtherSha1 : measurement.Sha1,
            changed == "md5" ? Md5.Parse(new string('e', 32)) : measurement.Md5,
            changed == "empty-crc" ? default : changed == "crc" ? Crc32.FromUInt32(124) : measurement.Crc32,
            changed == "profile" ? "different-profile/v1" : measurement.Profile,
            changed == "decoder" ? "different-decoder" : measurement.DecoderBuild,
            changed == "verifier" ? "different-worker" : measurement.VerifierBuild,
            changed == "time" ? measurement.VerifiedAt.AddTicks(1) : time ?? measurement.VerifiedAt,
            changed == "captured-profile" ? "different-observed-parameters" : measurement.CapturedEncodingProfile);

    private static RomChdIntegrity RestoreIntegrity(RomChdIntegrity fact, RomChdDecoderBuild? decoder = null) =>
        RomChdIntegrity.Rehydrate(fact.Id, fact.VerificationId, fact.ArtifactSha256, fact.ArtifactSize,
            fact.ContentSha256, fact.ContentSize, fact.Sha1, fact.Md5, fact.Crc32, fact.Parameters,
            fact.StoredRawSha1, fact.ComputedRawSha1, fact.StoredOverallSha1, fact.ComputedOverallSha1,
            decoder ?? fact.Decoder, fact.MeasurementProfile, fact.VerifierBuild, fact.MeasurementVerifiedAt,
            fact.Profile, fact.ObservedAt);
}
