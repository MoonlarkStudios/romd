using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomChdIntegrityTests
{
    [Fact]
    public void TryCreate_ImportedMeasuredChd_BindsExactObservationWithoutEncoderOrApproval()
    {
        var time = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(-5));
        var measured = ChdEvidenceSamples.Output(verifiedAt: time);
        var fact = ChdEvidenceSamples.Integrity(measured, time.AddMinutes(1));

        fact.Id.ShouldNotBe(Guid.Empty);
        fact.VerificationId.ShouldBe(measured.Id);
        fact.ArtifactSha256.ShouldBe(measured.ArtifactSha256);
        fact.ContentSha256.ShouldBe(measured.ContentSha256);
        fact.ArtifactSha256.ShouldNotBe(fact.ContentSha256);
        fact.ArtifactSize.ShouldBe(measured.ArtifactSize);
        fact.ContentSize.ShouldBe(measured.ContentSize);
        fact.StoredRawSha1.ShouldBe(measured.Sha1);
        fact.StoredOverallSha1.ShouldBe(ChdEvidenceSamples.OverallSha1);
        fact.StoredOverallSha1.ShouldNotBe(measured.Sha1);
        fact.Decoder.ShouldBe(ChdEvidenceSamples.Decoder());
        fact.Decoder.BuildLabel.ShouldBe("libchdr/linux-arm64/pin:" + ChdEvidenceSamples.OtherSha1 + "/recipe:1");
        fact.MeasurementProfile.ShouldBe(measured.Profile);
        fact.VerifierBuild.ShouldBe(measured.VerifierBuild);
        fact.MeasurementVerifiedAt.ShouldBe(time.ToUniversalTime());
        fact.MeasurementVerifiedAt.Offset.ShouldBe(TimeSpan.Zero);
        fact.ObservedAt.ShouldBe(time.AddMinutes(1).ToUniversalTime());
        fact.MatchesMeasurement(measured).ShouldBeTrue();
        RomVerification.Claim.ShouldBe("measured-output/v1");
        RomChdIntegrity.Claim.ShouldBe("container-integrity/v1");
    }

    [Theory]
    [InlineData("measurement")]
    [InlineData("parameters")]
    [InlineData("decoder")]
    [InlineData("profile")]
    [InlineData("stored-raw-empty")]
    [InlineData("computed-raw-empty")]
    [InlineData("stored-overall-empty")]
    [InlineData("computed-overall-empty")]
    [InlineData("stored-raw-mismatch")]
    [InlineData("stored-overall-mismatch")]
    [InlineData("decoded-raw-mismatch")]
    [InlineData("logical-length")]
    [InlineData("captured-profile")]
    [InlineData("missing-captured-profile")]
    [InlineData("decoder-pin")]
    [InlineData("decoder-recipe")]
    [InlineData("decoder-rid")]
    [InlineData("encoding")]
    [InlineData("empty-crc")]
    [InlineData("short-artifact")]
    public void TryCreate_AbsentOrContradictoryEvidence_ProducesNoIntegrity(string changed)
    {
        var measured = ChdEvidenceSamples.Output(changed);
        var decoder = ChdEvidenceSamples.Decoder(changed);
        Sha1 raw = ChdEvidenceSamples.LogicalSha1, overall = ChdEvidenceSamples.OverallSha1;
        bool accepted = RomChdIntegrity.TryCreate(changed == "measurement" ? null : measured,
            changed == "parameters" ? null : ChdEvidenceSamples.Parameters(),
            changed == "stored-raw-empty" ? default : changed is "stored-raw-mismatch" or "decoded-raw-mismatch" ? ChdEvidenceSamples.OtherSha1 : raw,
            changed == "computed-raw-empty" ? default : changed == "decoded-raw-mismatch" ? ChdEvidenceSamples.OtherSha1 : raw,
            changed == "stored-overall-empty" ? default : changed == "stored-overall-mismatch" ? ChdEvidenceSamples.OtherSha1 : overall,
            changed == "computed-overall-empty" ? default : overall,
            changed == "decoder" ? null : decoder,
            changed == "profile" ? "unknown-integrity/v1" : RomChdIntegrity.ProfileVersion, DateTimeOffset.UtcNow, out var fact);

        accepted.ShouldBeFalse();
        fact.ShouldBeNull();
    }

    [Theory]
    [InlineData("same-facts-new-id")]
    [InlineData("artifact-hash")]
    [InlineData("artifact-length")]
    [InlineData("logical-hash")]
    [InlineData("logical-length")]
    [InlineData("logical-sha1")]
    [InlineData("logical-md5")]
    [InlineData("logical-crc")]
    [InlineData("decoder-label")]
    [InlineData("captured-profile")]
    [InlineData("measurement-profile")]
    [InlineData("verifier-build")]
    [InlineData("measurement-time")]
    public void MatchesMeasurement_ForeignMeasurementCannotBorrowIntegrity(string changed)
    {
        var original = ChdEvidenceSamples.Output();
        var fact = ChdEvidenceSamples.Integrity(original);
        long length = changed == "logical-length" ? fact.ContentSize + 2048 : fact.ContentSize;
        var parameters = new RomChdContainerParameters(5, length, 16384, 2048, length == fact.ContentSize ? 8U : 9U,
            0x7a6c6962, 0, 0, 0, false, fact.Parameters.Metadata,
            changed == "captured-profile" ? "other-profile" : ChdEvidenceSamples.CapturedProfile);
        Sha1 sha1 = changed == "logical-sha1" ? ChdEvidenceSamples.OtherSha1 : fact.Sha1;
        var retained = RomChdIntegrity.Rehydrate(fact.Id, changed == "same-facts-new-id" ? Guid.NewGuid() : fact.VerificationId,
            changed == "artifact-hash" ? ChdEvidenceSamples.OtherHash : fact.ArtifactSha256,
            changed == "artifact-length" ? fact.ArtifactSize + 1 : fact.ArtifactSize,
            changed == "logical-hash" ? ChdEvidenceSamples.OtherHash : fact.ContentSha256, length, sha1,
            changed == "logical-md5" ? Md5.Parse(new string('e', 32)) : fact.Md5,
            changed == "logical-crc" ? Crc32.FromUInt32(124) : fact.Crc32, parameters, sha1, sha1,
            fact.StoredOverallSha1, fact.ComputedOverallSha1,
            changed == "decoder-label" ? ChdEvidenceSamples.Decoder("decoder-pin") : fact.Decoder,
            changed == "measurement-profile" ? "other-measurement/v1" : fact.MeasurementProfile,
            changed == "verifier-build" ? "other-verifier" : fact.VerifierBuild,
            changed == "measurement-time" ? fact.MeasurementVerifiedAt.AddTicks(1) : fact.MeasurementVerifiedAt,
            fact.Profile, fact.ObservedAt);

        if (changed is "measurement-profile" or "verifier-build" or "measurement-time")
            retained.VerificationId.ShouldBe(original.Id);
        retained.MatchesMeasurement(original).ShouldBeFalse("the original ID cannot mask changed retained facts");
        fact.MatchesMeasurement(null).ShouldBeFalse();
        fact.MatchesMeasurement(original).ShouldBeTrue();
    }

    [Fact]
    public void TryCreate_ActualZeroCrc_IsCompleteEvidence()
    {
        var measured = ChdEvidenceSamples.Output("zero-crc");
        ChdEvidenceSamples.Integrity(measured).Crc32.ShouldBe(Crc32.FromUInt32(0));
    }

    [Fact]
    public void Rehydrate_ExactScalarFacts_PreservesOriginalIdsAndTime()
    {
        var measured = ChdEvidenceSamples.Output();
        var fact = ChdEvidenceSamples.Integrity(measured);
        var retained = Rehydrate(fact);
        retained.ShouldBe(fact);
        retained.MatchesMeasurement(measured).ShouldBeTrue();
    }

    [Fact]
    public void Rehydrate_EquivalentMeasurementTimestampOffset_MatchesSameUtcInstant()
    {
        var measured = ChdEvidenceSamples.Output();
        var fact = ChdEvidenceSamples.Integrity(measured);
        var retained = Rehydrate(fact, "measurement-time-offset");

        retained.MeasurementVerifiedAt.Offset.ShouldBe(TimeSpan.Zero);
        retained.MeasurementVerifiedAt.ShouldBe(measured.VerifiedAt);
        retained.ShouldBe(fact);
        retained.MatchesMeasurement(measured).ShouldBeTrue();
    }

    [Theory]
    [InlineData("measurement-profile-control")]
    [InlineData("verifier-build-control")]
    public void TryCreate_ControlCharactersInMeasurementLabels_ProducesNoIntegrity(string changed)
    {
        var measured = ChdEvidenceSamples.Output(changed);
        RomChdIntegrity.TryCreate(measured, ChdEvidenceSamples.Parameters(), measured.Sha1, measured.Sha1,
            ChdEvidenceSamples.OverallSha1, ChdEvidenceSamples.OverallSha1, ChdEvidenceSamples.Decoder(),
            RomChdIntegrity.ProfileVersion, DateTimeOffset.UtcNow, out var fact).ShouldBeFalse();
        fact.ShouldBeNull();
    }

    [Fact]
    public void TryCreate_MaximumBoundedMeasurementLabels_PreservesExactValues()
    {
        var measured = ChdEvidenceSamples.Output("maximum-measurement-labels");
        var fact = ChdEvidenceSamples.Integrity(measured);

        fact.MeasurementProfile.ShouldBe(new string('p', 128));
        fact.VerifierBuild.ShouldBe(new string('v', 256));
        Rehydrate(fact).MatchesMeasurement(measured).ShouldBeTrue();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("verification")]
    [InlineData("raw")]
    [InlineData("overall")]
    [InlineData("content")]
    [InlineData("length")]
    [InlineData("measurement-profile-null")]
    [InlineData("measurement-profile-blank")]
    [InlineData("measurement-profile-limit")]
    [InlineData("measurement-profile-control")]
    [InlineData("verifier-build-null")]
    [InlineData("verifier-build-blank")]
    [InlineData("verifier-build-limit")]
    [InlineData("verifier-build-control")]
    public void Rehydrate_InvalidRetainedFacts_RejectsInsteadOfInventingProof(string changed) =>
        Should.Throw<ArgumentException>(() => Rehydrate(ChdEvidenceSamples.Integrity(ChdEvidenceSamples.Output()), changed));

    [Theory]
    [InlineData("version")]
    [InlineData("parent")]
    [InlineData("logical-zero")]
    [InlineData("logical-limit")]
    [InlineData("logical-alignment")]
    [InlineData("hunk-zero")]
    [InlineData("hunk-limit")]
    [InlineData("hunk-alignment")]
    [InlineData("unit")]
    [InlineData("count")]
    [InlineData("count-limit")]
    [InlineData("cd-codec")]
    [InlineData("unknown-codec")]
    [InlineData("profile-control")]
    [InlineData("profile-limit")]
    public void Parameters_UnboundedOrUnsupportedObservedShape_Rejects(string changed) =>
        Should.Throw<ArgumentException>(() => ChdEvidenceSamples.Parameters(changed));

    [Fact]
    public void Parameters_AbsentMetadata_CannotInferDvdFromGeometry() =>
        Should.Throw<ArgumentNullException>(() => ChdEvidenceSamples.Parameters("metadata"));

    [Theory]
    [InlineData(0, 0x63647a6cU)]
    [InlineData(1, 0x63647a6cU)]
    [InlineData(2, 0x63647a6cU)]
    [InlineData(3, 0x63647a6cU)]
    [InlineData(0, uint.MaxValue)]
    [InlineData(1, uint.MaxValue)]
    [InlineData(2, uint.MaxValue)]
    [InlineData(3, uint.MaxValue)]
    public void Parameters_DisallowedCodecInAnySlot_RejectsOtherwiseValidContainer(int slot, uint codec)
    {
        uint[] codecs = [0x7a6c6962, 0, 0, 0];
        codecs[slot] = codec;
        Should.Throw<ArgumentException>(() => new RomChdContainerParameters(5, ChdEvidenceSamples.LogicalBytes,
            16384, 2048, 8, codecs[0], codecs[1], codecs[2], codecs[3], false,
            ChdEvidenceSamples.Metadata(), ChdEvidenceSamples.CapturedProfile));
    }

    [Fact]
    public void Parameters_MaximumGeometryAndPartialFinalHunk_UseBoundedQuotientRemainder()
    {
        new RomChdContainerParameters(5, 10L << 30, 4096, 2048, 2621440,
            0, 0, 0, 0, false, ChdEvidenceSamples.Metadata(), "observed-maximum")
            .HunkCount.ShouldBe(2621440U);
        new RomChdContainerParameters(5, ChdEvidenceSamples.LogicalBytes + 2048, 16384, 2048, 9,
            0x7a6c6962, 0, 0, 0, false, ChdEvidenceSamples.Metadata(), "observed-remainder")
            .HunkCount.ShouldBe(9U);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("tag")]
    [InlineData("flags")]
    [InlineData("payload-length")]
    [InlineData("payload-byte")]
    [InlineData("profile")]
    [InlineData("fingerprint")]
    public void Metadata_AnyDifferentObservation_RejectsSingletonDvdClaim(string changed) =>
        Should.Throw<ArgumentException>(() => ChdEvidenceSamples.Metadata(changed));

    [Theory]
    [InlineData("rid")]
    [InlineData("rid-path")]
    [InlineData("rid-limit")]
    [InlineData("upstream-commit")]
    [InlineData("recipe")]
    public void Decoder_MissingOrAlteredTuple_Rejects(string changed) =>
        Should.Throw<ArgumentException>(() => ChdEvidenceSamples.Decoder(changed));

    private static RomChdIntegrity Rehydrate(RomChdIntegrity fact, string? changed = null) =>
        RomChdIntegrity.Rehydrate(changed == "id" ? Guid.Empty : fact.Id,
            changed == "verification" ? Guid.Empty : fact.VerificationId, fact.ArtifactSha256, fact.ArtifactSize,
            changed == "content" ? default : fact.ContentSha256, changed == "length" ? fact.ContentSize + 2048 : fact.ContentSize,
            fact.Sha1, fact.Md5, fact.Crc32, fact.Parameters,
            changed == "raw" ? ChdEvidenceSamples.OtherSha1 : fact.StoredRawSha1, fact.ComputedRawSha1,
            changed == "overall" ? ChdEvidenceSamples.OtherSha1 : fact.StoredOverallSha1, fact.ComputedOverallSha1,
            fact.Decoder,
            changed switch
            {
                "measurement-profile-null" => null!,
                "measurement-profile-blank" => " ",
                "measurement-profile-limit" => new string('p', 129),
                "measurement-profile-control" => "profile\n",
                _ => fact.MeasurementProfile
            },
            changed switch
            {
                "verifier-build-null" => null!,
                "verifier-build-blank" => " ",
                "verifier-build-limit" => new string('v', 257),
                "verifier-build-control" => "verifier\n",
                _ => fact.VerifierBuild
            },
            changed == "measurement-time-offset" ? fact.MeasurementVerifiedAt.ToOffset(TimeSpan.FromHours(-5)) : fact.MeasurementVerifiedAt,
            fact.Profile, fact.ObservedAt);
}

internal static class ChdEvidenceSamples
{
    internal const long LogicalBytes = 64 * 2048;
    internal const string CapturedProfile = "dvd;unit=2048;hunk=16384;codec=zlib;processors=1";
    internal static readonly Sha256 Content = Sha256.Parse(new string('a', 64));
    internal static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));
    internal static readonly Sha256 OtherHash = Sha256.Parse(new string('e', 64));
    internal static readonly Sha1 LogicalSha1 = Sha1.Parse(new string('c', 40));
    internal static readonly Sha1 OverallSha1 = Sha1.Parse(new string('f', 40));
    internal static readonly Sha1 OtherSha1 = Sha1.Parse(new string('e', 40));
    internal static readonly Md5 LogicalMd5 = Md5.Parse(new string('d', 32));
    internal static readonly Crc32 LogicalCrc = Crc32.FromUInt32(123);

    internal static RomChdDvdMetadataFacts Metadata(string? changed = null) => new(
        changed == "count" ? 2 : 1, changed == "tag" ? 0x43485432U : 0x44564420U,
        changed == "flags" ? (byte)0 : (byte)1, changed == "payload-length" ? 2U : 1U,
        changed == "payload-byte" ? (byte)1 : (byte)0,
        changed == "profile" ? "unknown-metadata/v1" : RomChdDvdMetadataFacts.ProfileVersion,
        changed == "fingerprint" ? OtherHash : RomChdDvdMetadataFacts.SingletonFingerprint);

    internal static RomChdContainerParameters Parameters(string? changed = null) => new(
        changed == "version" ? 4U : 5U,
        changed == "logical-zero" ? 0 : changed == "logical-limit" ? (10L << 30) + 2048 : changed == "logical-alignment" ? LogicalBytes + 1 : LogicalBytes,
        changed == "hunk-zero" ? 0U : changed == "hunk-limit" ? (1U << 20) + 2048 : changed == "hunk-alignment" ? 16385U : 16384U,
        changed == "unit" ? 2448U : 2048U,
        changed == "count" ? 9U : changed == "count-limit" ? 2621441U : 8U,
        changed == "cd-codec" ? 0x63647a6cU : changed == "unknown-codec" ? uint.MaxValue : 0x7a6c6962U,
        0, 0, 0, changed == "parent", changed == "metadata" ? null! : Metadata(),
        changed == "profile-control" ? "profile\n" : changed == "profile-limit" ? new string('x', 513) : CapturedProfile);

    internal static RomChdDecoderBuild Decoder(string? changed = null) => new(
        changed == "rid" ? "Linux_ARM64" : changed == "rid-path" ? "../linux-arm64" : changed == "rid-limit" ? new string('x', 33)
            : changed == "decoder-rid" ? "win-x64" : "linux-arm64",
        changed == "upstream-commit" ? default : changed == "decoder-pin" ? LogicalSha1 : OtherSha1,
        changed == "recipe" ? 0 : changed == "decoder-recipe" ? 2 : 1);

    internal static RomVerification Output(string? changed = null, RomChdDecoderBuild? decoder = null, DateTimeOffset? verifiedAt = null) => new(
        changed == "artifact-hash" ? OtherHash : Artifact,
        changed == "artifact-length" ? 4097 : changed == "short-artifact" ? 123 : 4096,
        changed == "encoding" ? RomRepresentationEncoding.Rvz : RomRepresentationEncoding.Chd,
        changed == "logical-hash" ? OtherHash : Content, changed == "logical-length" ? LogicalBytes + 2048 : LogicalBytes,
        changed == "logical-sha1" ? OtherSha1 : LogicalSha1,
        changed == "logical-md5" ? Md5.Parse(new string('e', 32)) : LogicalMd5,
        changed == "empty-crc" ? default : changed == "logical-crc" ? Crc32.FromUInt32(124) : changed == "zero-crc" ? Crc32.FromUInt32(0) : LogicalCrc,
        changed == "maximum-measurement-labels" ? new string('p', 128) : changed == "measurement-profile-control" ? "profile\n" : "chd-import/v1",
        changed == "decoder-label" ? "other-decoder" : (decoder ?? Decoder()).BuildLabel,
        changed == "maximum-measurement-labels" ? new string('v', 256) : changed == "verifier-build-control" ? "verifier\n" : "verifier/mvid:synthetic",
        verifiedAt ?? DateTimeOffset.UtcNow,
        changed == "missing-captured-profile" ? null : changed == "captured-profile" ? "other-profile" : CapturedProfile);

    internal static RomChdIntegrity Integrity(RomVerification output, DateTimeOffset? time = null)
    {
        RomChdIntegrity.TryCreate(output, Parameters(), LogicalSha1, LogicalSha1, OverallSha1, OverallSha1,
            Decoder(), RomChdIntegrity.ProfileVersion, time ?? DateTimeOffset.UtcNow, out var integrity).ShouldBeTrue();
        return integrity.ShouldNotBeNull();
    }

    internal static RomChdEncoderBuild Encoder(string? changed = null) => new(
        changed == "rid" ? "Linux_ARM64" : "linux-arm64",
        changed == "tag" ? "mame 0289" : changed == "tag-limit" ? new string('m', 33) : "mame0289",
        changed == "source-hash" ? default : changed == "encoder-source" ? OtherHash : Content,
        changed == "recipe" ? 0 : changed == "maximum-recipe" ? int.MaxValue : 1);

    internal static RomVerification Source() => new(Content, LogicalBytes, RomRepresentationEncoding.Identity,
        Content, LogicalBytes, LogicalSha1, LogicalMd5, LogicalCrc, "identity-output/v1", "hasher", "verifier", DateTimeOffset.UtcNow);

    internal static RomPs2BootLayoutFacts Boot() => new(LogicalBytes, 64, 16, 24, 2048, 25, 60, 26, 512,
        "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x00100000, 1,
        RomPs2BootLayoutFacts.ProfileVersion, "ps2-inspector/mvid:synthetic");

    internal static readonly Guid ReferenceRecordId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    internal static RomDvdMediaReference DvdReference(string? changed = null) => new(
        changed == "record-id" ? Guid.Parse("22222222-2222-3333-4444-555555555555") : ReferenceRecordId,
        changed == "hash" ? OtherHash : Content, changed == "length" ? LogicalBytes + 2048 : LogicalBytes,
        changed == "sha1" ? OtherSha1 : LogicalSha1, changed == "md5" ? Md5.Parse(new string('e', 32)) : LogicalMd5,
        changed == "crc" ? Crc32.FromUInt32(124) : LogicalCrc,
        changed == "unknown-kind" ? (RomDvdMediaKind)2 : changed == "dvd9" ? RomDvdMediaKind.Dvd9 : RomDvdMediaKind.Dvd5,
        "dvd-reference/v1", OtherHash, "synthetic-independent-reference");

    internal static RomPs2DvdEligibilityFacts Eligibility(string? changed = null) => new(Content, LogicalBytes,
        LogicalSha1, LogicalMd5, LogicalCrc, changed == "missing-boot" ? null! : Boot(),
        changed == "missing-reference" ? null! : DvdReference(changed));
}
