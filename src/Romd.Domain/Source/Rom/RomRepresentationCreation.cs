using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>A historical source witness. Source cleanup cannot change the independently compared target identity.</summary>
public sealed record RomCreationSource
{
    public RomCreationSource(int targetOutputId, int representationId, int outputId, Guid verificationId, int fileId,
        Sha256 artifactSha256, long artifactSize)
    {
        if (targetOutputId <= 0 || representationId <= 0 || outputId <= 0 || verificationId == Guid.Empty || fileId <= 0 ||
            artifactSha256.IsEmpty || artifactSize < 0)
            throw new ArgumentException("A creation source requires an exact historical witness.");
        TargetOutputId = targetOutputId;
        RepresentationId = representationId;
        OutputId = outputId;
        VerificationId = verificationId;
        FileId = fileId;
        ArtifactSha256 = artifactSha256;
        ArtifactSize = artifactSize;
    }
    public int TargetOutputId { get; }
    public int RepresentationId { get; }
    public int OutputId { get; }
    public Guid VerificationId { get; }
    public int FileId { get; }
    public Sha256 ArtifactSha256 { get; }
    public long ArtifactSize { get; }
}

/// <summary>One encoding operation and the independently compared sources of every measured output.</summary>
public sealed class RomRepresentationCreation
{
    public RomRepresentationCreation(Guid id, Guid verificationId, RecipeId recipe, string encoderBuild, DateTimeOffset comparedAt,
        Sha256 parametersSha256, RomChdEncoderBuild? nativeEncoder, IReadOnlyList<RomCreationSource> sources, RomCdCreationObservation? cd = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(sources);
        RomFactLabels.Validate(encoderBuild, 256);
        var captured = sources.ToArray();
        if (id == Guid.Empty || verificationId == Guid.Empty || parametersSha256.IsEmpty || captured.Length is < 1 or > 199 ||
            captured.Any(source => source is null) ||
            captured.Select(source => source.TargetOutputId).Distinct().Count() != captured.Length ||
            nativeEncoder is not null && encoderBuild != nativeEncoder.BuildLabel)
            throw new ArgumentException("Creation requires complete compared source witnesses and encoder provenance.");
        Id = id;
        VerificationId = verificationId;
        Recipe = recipe;
        EncoderBuild = encoderBuild;
        ComparedAt = comparedAt.ToUniversalTime();
        ParametersSha256 = parametersSha256;
        NativeEncoder = nativeEncoder;
        Sources = Array.AsReadOnly(captured);
        Cd = cd;
    }
    public Guid Id { get; }
    public Guid VerificationId { get; }
    public RecipeId Recipe { get; }
    public string EncoderBuild { get; }
    public DateTimeOffset ComparedAt { get; }
    public Sha256 ParametersSha256 { get; }
    public RomChdEncoderBuild? NativeEncoder { get; }
    public IReadOnlyList<RomCreationSource> Sources { get; }
    public RomCdCreationObservation? Cd { get; }
}
