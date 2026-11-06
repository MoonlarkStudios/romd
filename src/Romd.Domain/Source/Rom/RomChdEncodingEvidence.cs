using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>The encoder build that created a CHD: its RID, MAME source pin and chdman recipe version.</summary>
/// <remarks>Binary bytes are deliberately absent; shape only, never canonical recipe approval.</remarks>
public sealed record RomChdEncoderBuild
{
    public RomChdEncoderBuild(string rid, string mameTag, Sha256 mameSourceSha256, int recipeVersion)
    {
        if (!RomNativeBuildLabels.IsRid(rid) || !RomNativeBuildLabels.IsRid(mameTag) || mameSourceSha256.IsEmpty || recipeVersion < 1)
            throw new ArgumentException("CHD encoding evidence requires a bounded RID, a MAME source pin and a recipe version.");
        Rid = rid;
        MameTag = mameTag;
        MameSourceSha256 = mameSourceSha256;
        RecipeVersion = recipeVersion;
    }

    public string Rid { get; }
    public string MameTag { get; }
    public Sha256 MameSourceSha256 { get; }
    public int RecipeVersion { get; }
    public string BuildLabel => $"chdman/{Rid}/{MameTag}/source:{MameSourceSha256}/recipe:{RecipeVersion.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Created-output provenance augments exact source equivalence and independently measured integrity.</summary>
/// <remarks>Imported CHD integrity alone never invents this record. Encoder/native identities may have different RIDs.</remarks>
public sealed record RomChdEncodingEvidence
{
    public const string Claim = "chd-encoding-provenance/v1";

    private RomChdEncodingEvidence(Guid sourceEquivalenceId, Guid integrityId, Guid outputVerificationId,
        Sha256 sourceArtifactSha256, long sourceArtifactSize, Sha256 outputArtifactSha256, long outputArtifactSize,
        Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        RecipeId recipe, RomChdEncoderBuild encoder, RomChdContainerParameters parameters, DateTimeOffset encodedAt)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(parameters);
        if (sourceEquivalenceId == Guid.Empty || integrityId == Guid.Empty || outputVerificationId == Guid.Empty ||
            sourceArtifactSha256.IsEmpty || outputArtifactSha256.IsEmpty || contentSha256.IsEmpty || sha1.IsEmpty || md5.IsEmpty || crc32.IsEmpty ||
            sourceArtifactSize <= 0 || outputArtifactSize is < 124 or > RomChdDvdLimits.MaximumArtifactBytes || contentSize != parameters.LogicalBytes)
            throw new ArgumentException("Created CHD evidence requires exact comparison, integrity and complete artifact/output identities.");
        SourceEquivalenceId = sourceEquivalenceId;
        IntegrityId = integrityId;
        OutputVerificationId = outputVerificationId;
        SourceArtifactSha256 = sourceArtifactSha256;
        SourceArtifactSize = sourceArtifactSize;
        OutputArtifactSha256 = outputArtifactSha256;
        OutputArtifactSize = outputArtifactSize;
        ContentSha256 = contentSha256;
        ContentSize = contentSize;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
        Recipe = recipe;
        Encoder = encoder;
        Parameters = parameters;
        EncodedAt = encodedAt.ToUniversalTime();
    }

    public Guid SourceEquivalenceId { get; }
    public Guid IntegrityId { get; }
    public Guid OutputVerificationId { get; }
    public Sha256 SourceArtifactSha256 { get; }
    public long SourceArtifactSize { get; }
    public Sha256 OutputArtifactSha256 { get; }
    public long OutputArtifactSize { get; }
    public Sha256 ContentSha256 { get; }
    public long ContentSize { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
    public RecipeId Recipe { get; }
    public string EncoderRecipe => Recipe.Value;
    public RomChdEncoderBuild Encoder { get; }
    public RomChdContainerParameters Parameters { get; }
    public DateTimeOffset EncodedAt { get; }

    public static bool TryCreate(RomSourceEquivalence? independentComparison, RomVerification? independentOutput,
        RomChdIntegrity? outputIntegrity, RecipeId? recipe, RomChdEncoderBuild? encoder, DateTimeOffset encodedAt,
        [NotNullWhen(true)] out RomChdEncodingEvidence? evidence)
    {
        evidence = null;
        if (independentComparison is null || independentOutput is not { Encoding: RomRepresentationEncoding.Chd } ||
            outputIntegrity is null || recipe is null || encoder is null ||
            !independentComparison.MatchesOutput(independentOutput) || !outputIntegrity.MatchesMeasurement(independentOutput) ||
            independentComparison.EncoderRecipe != recipe.Value || independentComparison.EncoderBuild != encoder.BuildLabel ||
            independentComparison.SourceArtifactSize <= 0)
            return false;
        evidence = new(independentComparison.Id, outputIntegrity.Id, independentOutput.Id,
            independentComparison.SourceArtifactSha256, independentComparison.SourceArtifactSize,
            independentOutput.ArtifactSha256, independentOutput.ArtifactSize, independentOutput.ContentSha256,
            independentOutput.ContentSize, independentOutput.Sha1, independentOutput.Md5, independentOutput.Crc32,
            recipe, encoder, outputIntegrity.Parameters, encodedAt);
        return true;
    }

    public bool MatchesComparison(RomSourceEquivalence? comparison) => comparison is not null &&
        comparison.Id == SourceEquivalenceId && comparison.OutputEncoding == RomRepresentationEncoding.Chd &&
        comparison.OutputVerificationId == OutputVerificationId && comparison.SourceArtifactSha256 == SourceArtifactSha256 &&
        comparison.SourceArtifactSize == SourceArtifactSize && comparison.OutputArtifactSha256 == OutputArtifactSha256 &&
        comparison.OutputArtifactSize == OutputArtifactSize && comparison.SourceContentSha256 == ContentSha256 &&
        comparison.SourceContentSize == ContentSize && comparison.SourceSha1 == Sha1 && comparison.SourceMd5 == Md5 &&
        comparison.SourceCrc32 == Crc32 && comparison.EncoderRecipe == Recipe.Value && comparison.EncoderBuild == Encoder.BuildLabel;

    public static RomChdEncodingEvidence Rehydrate(Guid sourceEquivalenceId, Guid integrityId, Guid outputVerificationId,
        Sha256 sourceArtifactSha256, long sourceArtifactSize, Sha256 outputArtifactSha256, long outputArtifactSize,
        Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        RecipeId recipe, RomChdEncoderBuild encoder, RomChdContainerParameters parameters, DateTimeOffset encodedAt) =>
        new(sourceEquivalenceId, integrityId, outputVerificationId, sourceArtifactSha256, sourceArtifactSize,
            outputArtifactSha256, outputArtifactSize, contentSha256, contentSize, sha1, md5, crc32, recipe, encoder, parameters, encodedAt);
}
