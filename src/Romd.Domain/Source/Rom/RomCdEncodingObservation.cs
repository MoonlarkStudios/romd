using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>Actual source-request and PS1 boot observations from an independently verified CD encoding operation.</summary>
public sealed class RomCdEncodingObservation
{
    public RomCdEncodingObservation(RomMeasuredRepresentation measured, RecipeId recipe, RomChdEncoderBuild encoder,
        Sha256 sourceRequestSha256, long sourceRequestBytes, string inspectorBuild, RomPs1CdBoot boot)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(boot);
        RomFactLabels.Validate(inspectorBuild, 256);
        if (measured.Manifest is not RomReconstructionManifest.CdTracks cd || measured.Container is not RomContainerFacts.Chd ||
            measured.Measurement.Decoder is not RomDecoderIdentity.Native || sourceRequestSha256.IsEmpty || sourceRequestBytes is < 1 or > (512 << 10))
            throw new ArgumentException("CD encoding requires complete native measurement, exact request custody and observed boot facts.");
        if (!RomCdBootInspection.Fits(cd, boot))
            throw new ArgumentException("Observed PS1 extents must fit their independently measured first data track.");
        Id = Guid.NewGuid();
        MeasurementId = measured.Measurement.Id;
        Recipe = recipe;
        Encoder = encoder;
        SourceRequestSha256 = sourceRequestSha256;
        SourceRequestBytes = sourceRequestBytes;
        InspectorBuild = inspectorBuild;
        Boot = boot;
    }
    public Guid Id { get; }
    public Guid MeasurementId { get; }
    public RecipeId Recipe { get; }
    public RomChdEncoderBuild Encoder { get; }
    public Sha256 SourceRequestSha256 { get; }
    public long SourceRequestBytes { get; }
    public string InspectorBuild { get; }
    public RomPs1CdBoot Boot { get; }
}
