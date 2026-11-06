using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomSourceEquivalenceTests
{
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));
    private static readonly Sha1 LogicalSha1 = Sha1.Parse(new string('c', 40));
    private static readonly Md5 LogicalMd5 = Md5.Parse(new string('d', 32));
    private static readonly Crc32 LogicalCrc32 = Crc32.FromUInt32(123);

    [Fact]
    public void TryCreate_IndependentExactOutput_BindsSourceHashesAndExactOutputMeasurement()
    {
        var source = Source();
        var output = Output();
        var time = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(-5));
        RomSourceEquivalence.TryCreate(source, output, "rvz-canonical/v1", "encoder/mvid:test", time, out var claim).ShouldBeTrue();
        claim.ShouldNotBeNull().OutputVerificationId.ShouldBe(output.Id);
        claim.SourceArtifactSha256.ShouldBe(Logical);
        claim.SourceArtifactSize.ShouldBe(100);
        claim.SourceContentSha256.ShouldBe(Logical);
        claim.SourceContentSize.ShouldBe(100);
        claim.SourceSha1.ShouldBe(LogicalSha1);
        claim.SourceMd5.ShouldBe(LogicalMd5);
        claim.SourceCrc32.ShouldBe(LogicalCrc32);
        claim.OutputEncoding.ShouldBe(RomRepresentationEncoding.Rvz);
        claim.OutputArtifactSha256.ShouldBe(Artifact);
        claim.OutputArtifactSize.ShouldBe(20);
        claim.ComparedAt.ShouldBe(time.ToUniversalTime());
        claim.MatchesOutput(output).ShouldBeTrue();
        claim.MatchesOutput(Output()).ShouldBeFalse("another measurement ID is not the bound independent comparison");
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("length")]
    public void TryCreate_AnyLogicalMismatch_ReturnsFalseWithoutEvidence(string changed)
    {
        var output = new RomVerification(Artifact, 20, RomRepresentationEncoding.Rvz,
            changed == "sha256" ? Sha256.Parse(new string('e', 64)) : Logical, changed == "length" ? 101 : 100,
            changed == "sha1" ? Sha1.Parse(new string('e', 40)) : LogicalSha1,
            changed == "md5" ? Md5.Parse(new string('e', 32)) : LogicalMd5,
            changed == "crc32" ? Crc32.FromUInt32(124) : LogicalCrc32,
            "rvz-disc-supervised/v1", "decoder", "verifier", DateTimeOffset.UtcNow);
        RomSourceEquivalence.TryCreate(Source(), output, "rvz-canonical/v1", "encoder", DateTimeOffset.UtcNow, out var claim)
            .ShouldBeFalse();
        claim.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, "encoder")]
    [InlineData("", "encoder")]
    [InlineData(" ", "encoder")]
    [InlineData("recipe", null)]
    [InlineData("recipe", " ")]
    public void TryCreate_InvalidProvenance_ReturnsFalse(string? recipe, string? encoder)
    {
        RomSourceEquivalence.TryCreate(Source(), Output(), recipe, encoder, DateTimeOffset.UtcNow, out var claim).ShouldBeFalse();
        claim.ShouldBeNull();
    }

    [Fact]
    public void TryCreate_MissingReusedOrIdentityOutput_RejectsComparison()
    {
        var output = Output();
        RomSourceEquivalence.TryCreate(output, output, "recipe", "encoder", DateTimeOffset.UtcNow, out _).ShouldBeFalse();
        RomSourceEquivalence.TryCreate(null, output, "recipe", "encoder", DateTimeOffset.UtcNow, out _).ShouldBeFalse();
        RomSourceEquivalence.TryCreate(Source(), null, "recipe", "encoder", DateTimeOffset.UtcNow, out _).ShouldBeFalse();
        RomSourceEquivalence.TryCreate(Source(), Source(), "recipe", "encoder", DateTimeOffset.UtcNow, out _).ShouldBeFalse();
        RomSourceEquivalence.TryCreate(Source(), output, new string('x', 129), "encoder", DateTimeOffset.UtcNow, out _).ShouldBeFalse();
        RomSourceEquivalence.TryCreate(Source(), output, "recipe", new string('x', 257), DateTimeOffset.UtcNow, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("recipe\n", "encoder")]
    [InlineData("recipe", "encoder\0")]
    [InlineData("recipe", "encoder\u007f/mvid:test")]
    public void TryCreate_ControlProvenance_ReturnsFalseWithoutComparison(string recipe, string build)
    {
        RomSourceEquivalence.TryCreate(Source(), Output(), recipe, build, DateTimeOffset.UtcNow, out var comparison).ShouldBeFalse();
        comparison.ShouldBeNull();
    }

    [Fact]
    public void TryCreate_ChdIndependentOutput_PreservesGenericComparisonAndEncodingValues()
    {
        var source = ChdEvidenceSamples.Source();
        var output = ChdEvidenceSamples.Output();
        var encoder = ChdEvidenceSamples.Encoder();
        RomSourceEquivalence.TryCreate(source, output, "chd-dvd-experiment/v1", encoder.BuildLabel,
            DateTimeOffset.UtcNow, out var comparison).ShouldBeTrue();
        comparison.ShouldNotBeNull().OutputEncoding.ShouldBe(RomRepresentationEncoding.Chd);
        comparison.SourceArtifactSha256.ShouldBe(source.ArtifactSha256);
        comparison.OutputArtifactSha256.ShouldBe(output.ArtifactSha256);
        comparison.MatchesOutput(output).ShouldBeTrue();
        ((int)RomRepresentationEncoding.Identity).ShouldBe(0);
        ((int)RomRepresentationEncoding.Rvz).ShouldBe(1);
        ((int)RomRepresentationEncoding.Chd).ShouldBe(2);
        ((int)RomIsoDiscKind.GameCube).ShouldBe(0);
        ((int)RomIsoDiscKind.Wii).ShouldBe(1);
        ((int)RomIsoDiscKind.PlayStation2Dvd).ShouldBe(2);
        RomRepresentationEncoding.Chd.IsEncoded().ShouldBeTrue();
        ((RomRepresentationEncoding)3).IsEncoded().ShouldBeFalse();
    }

    [Theory]
    [InlineData("logical-hash")]
    [InlineData("logical-length")]
    [InlineData("logical-sha1")]
    [InlineData("logical-md5")]
    [InlineData("logical-crc")]
    public void TryCreate_ChdAnyLogicalMismatch_DoesNotEstablishSourceEquivalence(string changed)
    {
        RomSourceEquivalence.TryCreate(ChdEvidenceSamples.Source(), ChdEvidenceSamples.Output(changed),
            "chd-dvd-experiment/v1", ChdEvidenceSamples.Encoder().BuildLabel, DateTimeOffset.UtcNow, out var comparison).ShouldBeFalse();
        comparison.ShouldBeNull();
    }

    [Fact]
    public void TryCreate_ChdReusedMeasurement_CannotStandInForIndependentDecode()
    {
        var measured = ChdEvidenceSamples.Output();
        RomSourceEquivalence.TryCreate(measured, measured, "chd-dvd-experiment/v1", ChdEvidenceSamples.Encoder().BuildLabel,
            DateTimeOffset.UtcNow, out var comparison).ShouldBeFalse();
        comparison.ShouldBeNull();
    }

    private static RomVerification Source() => new(Logical, 100, RomRepresentationEncoding.Identity,
        Logical, 100, LogicalSha1, LogicalMd5, LogicalCrc32, "identity-output/v1", "hasher", "verifier", DateTimeOffset.UtcNow);
    private static RomVerification Output() => new(Artifact, 20, RomRepresentationEncoding.Rvz,
        Logical, 100, LogicalSha1, LogicalMd5, LogicalCrc32, "rvz-disc-supervised/v1", "decoder", "verifier", DateTimeOffset.UtcNow);
}
