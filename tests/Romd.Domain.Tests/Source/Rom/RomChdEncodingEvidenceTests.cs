using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomChdEncodingEvidenceTests
{
    private const string Recipe = "chd-dvd-experiment/v1";

    [Fact]
    public void TryCreate_ExactIndependentComparisonAndIntegrity_PreservesCreatedOutputProvenance()
    {
        var output = ChdEvidenceSamples.Output();
        var integrity = ChdEvidenceSamples.Integrity(output);
        var encoder = ChdEvidenceSamples.Encoder();
        var comparison = Compare(output, encoder);
        var time = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(3));
        RomChdEncodingEvidence.TryCreate(comparison, output, integrity, RecipeId.Parse(Recipe), encoder, time, out var fact).ShouldBeTrue();

        fact.ShouldNotBeNull().SourceEquivalenceId.ShouldBe(comparison.Id);
        fact.IntegrityId.ShouldBe(integrity.Id);
        fact.OutputVerificationId.ShouldBe(output.Id);
        fact.SourceArtifactSha256.ShouldBe(ChdEvidenceSamples.Content);
        fact.OutputArtifactSha256.ShouldBe(ChdEvidenceSamples.Artifact);
        fact.ContentSha256.ShouldBe(ChdEvidenceSamples.Content);
        fact.EncoderRecipe.ShouldBe(Recipe);
        fact.Parameters.ShouldBe(integrity.Parameters);
        fact.Encoder.ShouldBe(encoder);
        fact.Encoder.BuildLabel.ShouldBe("chdman/linux-arm64/mame0289/source:" + ChdEvidenceSamples.Content + "/recipe:1");
        fact.EncodedAt.ShouldBe(time.ToUniversalTime());
        fact.MatchesComparison(comparison).ShouldBeTrue();
        RomSourceEquivalence.Claim.ShouldBe("source-equivalence/v1");
    }

    [Theory]
    [InlineData("comparison")]
    [InlineData("output")]
    [InlineData("integrity")]
    [InlineData("recipe")]
    [InlineData("encoder")]
    [InlineData("foreign-output")]
    [InlineData("foreign-integrity")]
    [InlineData("wrong-recipe")]
    [InlineData("wrong-encoder")]
    [InlineData("wrong-encoding")]
    public void TryCreate_MissingOrForeignEvidence_CannotCreateEncoderProvenance(string changed)
    {
        var output = ChdEvidenceSamples.Output();
        var integrity = ChdEvidenceSamples.Integrity(output);
        var encoder = ChdEvidenceSamples.Encoder();
        var comparison = Compare(output, encoder);
        bool accepted = RomChdEncodingEvidence.TryCreate(changed == "comparison" ? null : comparison,
            changed == "output" ? null : changed == "foreign-output" ? ChdEvidenceSamples.Output() :
                changed == "wrong-encoding" ? ChdEvidenceSamples.Output("encoding") : output,
            changed == "integrity" ? null : changed == "foreign-integrity" ? ChdEvidenceSamples.Integrity(ChdEvidenceSamples.Output()) : integrity,
            changed == "recipe" ? null : RecipeId.Parse(changed == "wrong-recipe" ? "chd-other/v1" : Recipe),
            changed == "encoder" ? null : changed == "wrong-encoder" ? ChdEvidenceSamples.Encoder("encoder-source") : encoder,
            DateTimeOffset.UtcNow, out var fact);

        accepted.ShouldBeFalse();
        fact.ShouldBeNull();
        integrity.MatchesMeasurement(output).ShouldBeTrue("imported integrity remains valid without invented encoding provenance");
    }

    [Theory]
    [InlineData("source-artifact")]
    [InlineData("source-length")]
    [InlineData("output-artifact")]
    [InlineData("output-length")]
    [InlineData("logical-hash")]
    [InlineData("logical-sha1")]
    [InlineData("logical-md5")]
    [InlineData("logical-crc")]
    [InlineData("recipe")]
    [InlineData("encoder")]
    public void MatchesComparison_RetainedIdWithContradictoryFacts_DoesNotBorrowParent(string changed)
    {
        var output = ChdEvidenceSamples.Output();
        var encoder = ChdEvidenceSamples.Encoder();
        var comparison = Compare(output, encoder);
        var integrity = ChdEvidenceSamples.Integrity(output);
        RomChdEncodingEvidence.TryCreate(comparison, output, integrity, RecipeId.Parse(Recipe), encoder,
            DateTimeOffset.UtcNow, out var original).ShouldBeTrue();
        var retained = Rehydrate(original!, changed);

        retained.SourceEquivalenceId.ShouldBe(comparison.Id);
        retained.MatchesComparison(comparison).ShouldBeFalse();
        original!.MatchesComparison(comparison).ShouldBeTrue();
    }

    [Fact]
    public void Rehydrate_ExactFacts_RetainsComparisonIntegrityAndMeasurementIds()
    {
        var output = ChdEvidenceSamples.Output();
        var encoder = ChdEvidenceSamples.Encoder();
        var comparison = Compare(output, encoder);
        RomChdEncodingEvidence.TryCreate(comparison, output, ChdEvidenceSamples.Integrity(output), RecipeId.Parse(Recipe), encoder,
            DateTimeOffset.UtcNow, out var original).ShouldBeTrue();
        Rehydrate(original!).ShouldBe(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchesComparison_DifferentNonemptyParentId_RejectsOtherwiseIdenticalFacts(bool measurementId)
    {
        var output = ChdEvidenceSamples.Output();
        var encoder = ChdEvidenceSamples.Encoder();
        var comparison = Compare(output, encoder);
        RomChdEncodingEvidence.TryCreate(comparison, output, ChdEvidenceSamples.Integrity(output), RecipeId.Parse(Recipe),
            encoder, DateTimeOffset.UtcNow, out var original).ShouldBeTrue();
        var fact = original!;
        var changed = RomChdEncodingEvidence.Rehydrate(measurementId ? fact.SourceEquivalenceId : Guid.NewGuid(),
            fact.IntegrityId, measurementId ? Guid.NewGuid() : fact.OutputVerificationId,
            fact.SourceArtifactSha256, fact.SourceArtifactSize, fact.OutputArtifactSha256, fact.OutputArtifactSize,
            fact.ContentSha256, fact.ContentSize, fact.Sha1, fact.Md5, fact.Crc32, fact.Recipe,
            fact.Encoder, fact.Parameters, fact.EncodedAt);

        changed.MatchesComparison(comparison).ShouldBeFalse();
        fact.MatchesComparison(comparison).ShouldBeTrue();
    }

    [Theory]
    [InlineData("comparison-id")]
    [InlineData("integrity-id")]
    [InlineData("measurement-id")]
    [InlineData("empty-source-hash")]
    [InlineData("empty-output-hash")]
    [InlineData("zero-source-length")]
    [InlineData("short-output")]
    [InlineData("logical-length")]
    public void Rehydrate_IncompleteScalarFacts_Rejects(string changed)
    {
        var output = ChdEvidenceSamples.Output();
        var encoder = ChdEvidenceSamples.Encoder();
        RomChdEncodingEvidence.TryCreate(Compare(output, encoder), output, ChdEvidenceSamples.Integrity(output), RecipeId.Parse(Recipe), encoder,
            DateTimeOffset.UtcNow, out var fact).ShouldBeTrue();
        Should.Throw<ArgumentException>(() => Rehydrate(fact!, changed));
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("tag")]
    [InlineData("tag-limit")]
    [InlineData("source-hash")]
    [InlineData("recipe")]
    public void Encoder_IncompleteOrUnrecognizedTuple_Rejects(string changed) =>
        Should.Throw<ArgumentException>(() => ChdEvidenceSamples.Encoder(changed));

    [Fact]
    public void Encoder_MaximumRecipe_LabelFitsExistingComparisonContract()
    {
        var encoder = ChdEvidenceSamples.Encoder("maximum-recipe");
        encoder.BuildLabel.Length.ShouldBeLessThanOrEqualTo(256);
        encoder.BuildLabel.ShouldEndWith("/recipe:2147483647");
        var output = ChdEvidenceSamples.Output();
        RomChdEncodingEvidence.TryCreate(Compare(output, encoder), output, ChdEvidenceSamples.Integrity(output),
            RecipeId.Parse(Recipe), encoder, DateTimeOffset.UtcNow, out _).ShouldBeTrue();
    }

    private static RomSourceEquivalence Compare(RomVerification output, RomChdEncoderBuild encoder)
    {
        RomSourceEquivalence.TryCreate(ChdEvidenceSamples.Source(), output, Recipe, encoder.BuildLabel,
            DateTimeOffset.UtcNow, out var comparison).ShouldBeTrue();
        return comparison.ShouldNotBeNull();
    }

    private static RomChdEncodingEvidence Rehydrate(RomChdEncodingEvidence fact, string? changed = null) =>
        RomChdEncodingEvidence.Rehydrate(changed == "comparison-id" ? Guid.Empty : fact.SourceEquivalenceId,
            changed == "integrity-id" ? Guid.Empty : fact.IntegrityId, changed == "measurement-id" ? Guid.Empty : fact.OutputVerificationId,
            changed == "empty-source-hash" ? default : changed == "source-artifact" ? ChdEvidenceSamples.OtherHash : fact.SourceArtifactSha256,
            changed == "zero-source-length" ? 0 : changed == "source-length" ? fact.SourceArtifactSize + 2048 : fact.SourceArtifactSize,
            changed == "empty-output-hash" ? default : changed == "output-artifact" ? ChdEvidenceSamples.OtherHash : fact.OutputArtifactSha256,
            changed == "short-output" ? 123 : changed == "output-length" ? fact.OutputArtifactSize + 1 : fact.OutputArtifactSize,
            changed == "logical-hash" ? ChdEvidenceSamples.OtherHash : fact.ContentSha256,
            changed == "logical-length" ? fact.ContentSize + 2048 : fact.ContentSize,
            changed == "logical-sha1" ? ChdEvidenceSamples.OtherSha1 : fact.Sha1,
            changed == "logical-md5" ? Md5.Parse(new string('e', 32)) : fact.Md5,
            changed == "logical-crc" ? Crc32.FromUInt32(124) : fact.Crc32,
            changed == "recipe" ? RecipeId.Parse("different/v1") : fact.Recipe,
            changed == "encoder" ? ChdEvidenceSamples.Encoder("encoder-source") : fact.Encoder, fact.Parameters, fact.EncodedAt);
}
