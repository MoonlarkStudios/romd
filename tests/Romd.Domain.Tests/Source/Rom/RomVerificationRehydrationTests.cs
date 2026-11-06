using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomVerificationRehydrationTests
{
    private static readonly DateTimeOffset MeasuredAt = new DateTimeOffset(2026, 10, 10, 7, 8, 9, TimeSpan.FromHours(-5)).AddTicks(1234567);

    [Theory]
    [InlineData(RomRepresentationEncoding.Identity)]
    [InlineData(RomRepresentationEncoding.Rvz)]
    [InlineData(RomRepresentationEncoding.Chd)]
    public void Rehydrate_CompleteMeasurement_RetainsEveryFieldAndIdentity(RomRepresentationEncoding encoding)
    {
        var measured = Measurement(encoding);

        var restored = Restore(measured);

        restored.ShouldNotBeSameAs(measured);
        restored.ShouldBe(measured);
        restored.Id.ShouldBe(measured.Id);
        restored.Id.ShouldNotBe(Guid.Empty);
        restored.ArtifactSha256.ShouldBe(measured.ArtifactSha256);
        restored.ArtifactSize.ShouldBe(measured.ArtifactSize);
        restored.Encoding.ShouldBe(encoding);
        restored.ContentSha256.ShouldBe(measured.ContentSha256);
        restored.ContentSize.ShouldBe(measured.ContentSize);
        restored.Sha1.ShouldBe(measured.Sha1);
        restored.Md5.ShouldBe(measured.Md5);
        restored.Crc32.ShouldBe(measured.Crc32);
        restored.Profile.ShouldBe(measured.Profile);
        restored.DecoderBuild.ShouldBe(measured.DecoderBuild);
        restored.VerifierBuild.ShouldBe(measured.VerifierBuild);
        restored.CapturedEncodingProfile.ShouldBe(measured.CapturedEncodingProfile);
        restored.VerifiedAt.UtcTicks.ShouldBe(MeasuredAt.UtcTicks);
        restored.VerifiedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Create_SameInputs_StillAllocatesDistinctObservationIds()
    {
        var first = Measurement();
        var second = Measurement();

        first.Id.ShouldNotBe(Guid.Empty);
        second.Id.ShouldNotBe(Guid.Empty);
        second.Id.ShouldNotBe(first.Id);
        second.ShouldNotBe(first);
    }

    [Fact]
    public void Rehydrate_OffsetAndSubMillisecondTime_RetainsExactUtcInstant()
    {
        var measured = Measurement();
        var offsetEquivalent = measured.VerifiedAt.ToOffset(TimeSpan.FromHours(3));

        Restore(measured, time: offsetEquivalent).ShouldBe(measured);
        var oneTickLater = Restore(measured, time: measured.VerifiedAt.AddTicks(1));
        oneTickLater.Id.ShouldBe(measured.Id);
        oneTickLater.VerifiedAt.UtcTicks.ShouldBe(measured.VerifiedAt.UtcTicks + 1);
        oneTickLater.ShouldNotBe(measured);
        (measured.VerifiedAt.UtcTicks % TimeSpan.TicksPerMillisecond).ShouldNotBe(0L);
    }

    [Fact]
    public void Rehydrate_ValidClockEndpoints_PreservesConstructorBehavior()
    {
        var measured = Measurement();

        Restore(measured, time: DateTimeOffset.MinValue).VerifiedAt.ShouldBe(DateTimeOffset.MinValue);
        Restore(measured, time: DateTimeOffset.MaxValue).VerifiedAt.ShouldBe(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void Rehydrate_UnknownHistoricalProfileAndCrc_DoesNotInventFacts()
    {
        var measured = Measurement(RomRepresentationEncoding.Rvz);
        var restored = Restore(measured, changed: "unknown-historical-facts");

        restored.Id.ShouldBe(measured.Id);
        restored.CapturedEncodingProfile.ShouldBeNull();
        restored.Crc32.IsEmpty.ShouldBeTrue();
        restored.VerifiedAt.UtcTicks.ShouldBe(measured.VerifiedAt.UtcTicks);
    }

    [Fact]
    public void Rehydrate_EmptyObservationId_RefusesToAllocateReplacement()
    {
        Should.Throw<ArgumentException>(() => Restore(Measurement(), id: Guid.Empty));
    }

    [Theory]
    [InlineData("artifact-hash")]
    [InlineData("artifact-length")]
    [InlineData("encoding")]
    [InlineData("content-hash")]
    [InlineData("content-length")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("identity-artifact-hash")]
    [InlineData("identity-artifact-length")]
    [InlineData("profile-blank")]
    [InlineData("profile-limit")]
    [InlineData("decoder-blank")]
    [InlineData("decoder-limit")]
    [InlineData("verifier-blank")]
    [InlineData("verifier-limit")]
    [InlineData("captured-blank")]
    [InlineData("captured-limit")]
    [InlineData("identity-captured-profile")]
    public void Rehydrate_InvalidRetainedFields_UsesExistingValidation(string changed)
    {
        Should.Throw<ArgumentException>(() => Restore(Measurement(), changed: changed));
    }

    private static RomVerification Measurement(RomRepresentationEncoding encoding = RomRepresentationEncoding.Identity) =>
        new(encoding == RomRepresentationEncoding.Identity ? ChdEvidenceSamples.Content : ChdEvidenceSamples.Artifact,
            encoding == RomRepresentationEncoding.Identity ? ChdEvidenceSamples.LogicalBytes : 4096, encoding,
            ChdEvidenceSamples.Content, ChdEvidenceSamples.LogicalBytes,
            ChdEvidenceSamples.LogicalSha1, ChdEvidenceSamples.LogicalMd5, Crc32.FromUInt32(0),
            "measurement/v1", "decoder/v1", "worker/mvid:synthetic", MeasuredAt,
            encoding == RomRepresentationEncoding.Identity ? RomEncodingProfiles.Identity : "observed-parameters/v1");

    private static RomVerification Restore(RomVerification measured, Guid? id = null, DateTimeOffset? time = null, string? changed = null) =>
        RomVerification.Rehydrate(id ?? measured.Id,
            changed == "artifact-hash" ? default : changed == "identity-artifact-hash" ? ChdEvidenceSamples.OtherHash : measured.ArtifactSha256,
            changed == "artifact-length" ? -1 : changed == "identity-artifact-length" ? measured.ArtifactSize + 1 : measured.ArtifactSize,
            changed == "encoding" ? (RomRepresentationEncoding)999 : measured.Encoding,
            changed == "content-hash" ? default : measured.ContentSha256,
            changed == "content-length" ? -1 : measured.ContentSize,
            changed == "sha1" ? default : measured.Sha1,
            changed == "md5" ? default : measured.Md5,
            changed == "unknown-historical-facts" ? default : measured.Crc32,
            changed == "profile-blank" ? " " : changed == "profile-limit" ? new string('p', 129) : measured.Profile,
            changed == "decoder-blank" ? " " : changed == "decoder-limit" ? new string('d', 257) : measured.DecoderBuild,
            changed == "verifier-blank" ? " " : changed == "verifier-limit" ? new string('v', 257) : measured.VerifierBuild,
            time ?? measured.VerifiedAt,
            changed == "unknown-historical-facts" ? null : changed == "captured-blank" ? " " :
                changed == "captured-limit" ? new string('c', 513) : changed == "identity-captured-profile" ? "different-profile" : measured.CapturedEncodingProfile);
}
