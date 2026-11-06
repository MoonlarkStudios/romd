using System.Diagnostics.CodeAnalysis;
using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>Exact source comparison attached to a measured output; source identity has no ownership dependency.</summary>
public sealed record RomSourceEquivalence
{
    private RomSourceEquivalence(RomVerification source, RomVerification output, string encoderRecipe,
        string encoderBuild, DateTimeOffset comparedAt)
    {
        SourceArtifactSha256 = source.ArtifactSha256;
        SourceArtifactSize = source.ArtifactSize;
        SourceContentSha256 = source.ContentSha256;
        SourceContentSize = source.ContentSize;
        SourceSha1 = source.Sha1;
        SourceMd5 = source.Md5;
        SourceCrc32 = source.Crc32;
        OutputEncoding = output.Encoding;
        OutputVerificationId = output.Id;
        OutputArtifactSha256 = output.ArtifactSha256;
        OutputArtifactSize = output.ArtifactSize;
        EncoderRecipe = encoderRecipe;
        EncoderBuild = encoderBuild;
        ComparedAt = comparedAt.ToUniversalTime();
    }

    public const string Claim = "source-equivalence/v1";
    public Guid Id { get; } = Guid.NewGuid();
    public Sha256 SourceArtifactSha256 { get; }
    public long SourceArtifactSize { get; }
    public Sha256 SourceContentSha256 { get; }
    public long SourceContentSize { get; }
    public Sha1 SourceSha1 { get; }
    public Md5 SourceMd5 { get; }
    public Crc32 SourceCrc32 { get; }
    public RomRepresentationEncoding OutputEncoding { get; }
    public Guid OutputVerificationId { get; }
    public Sha256 OutputArtifactSha256 { get; }
    public long OutputArtifactSize { get; }
    public string EncoderRecipe { get; }
    public string EncoderBuild { get; }
    public DateTimeOffset ComparedAt { get; }

    /// <summary>Measurement IDs prevent self-reuse; the caller must run output comparison independently.</summary>
    public static bool TryCreate(RomVerification? source, RomVerification? independentOutput, string? encoderRecipe,
        string? encoderBuild, DateTimeOffset comparedAt, [NotNullWhen(true)] out RomSourceEquivalence? equivalence)
    {
        equivalence = null;
        if (source is null || independentOutput is null || source.Id == independentOutput.Id ||
            !independentOutput.Encoding.IsEncoded() ||
            !RecipeId.TryParse(encoderRecipe, out _) || !ValidLabel(encoderBuild, 256) ||
            source.ContentSha256 != independentOutput.ContentSha256 || source.ContentSize != independentOutput.ContentSize ||
            source.Sha1 != independentOutput.Sha1 || source.Md5 != independentOutput.Md5 || source.Crc32 != independentOutput.Crc32)
            return false;
        equivalence = new(source, independentOutput, encoderRecipe!, encoderBuild!, comparedAt);
        return true;
    }

    public bool MatchesOutput(RomVerification output) => OutputVerificationId == output.Id &&
        OutputArtifactSha256 == output.ArtifactSha256 && OutputArtifactSize == output.ArtifactSize &&
        output.Encoding == OutputEncoding && SourceContentSha256 == output.ContentSha256 &&
        SourceContentSize == output.ContentSize && SourceSha1 == output.Sha1 && SourceMd5 == output.Md5 && SourceCrc32 == output.Crc32;

    private static bool ValidLabel(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
}
