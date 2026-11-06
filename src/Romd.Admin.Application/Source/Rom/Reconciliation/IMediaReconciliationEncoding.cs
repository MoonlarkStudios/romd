using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Fresh encoder invocation and independently compared output. Its lease owns every payload until publication returns.</summary>
public abstract class MediaReconciliationEncoded
{
    private MediaReconciliationEncoded(ITempFile artifact, TempFileArtifactMeasurement artifactMeasurement, RomMeasuredRepresentation measured, RecipeId recipe, string encoderBuild, DateTimeOffset comparedAt)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(artifactMeasurement);
        ArgumentNullException.ThrowIfNull(recipe);
        if (artifactMeasurement.Sha256.IsEmpty || artifactMeasurement.Md5.IsEmpty || artifactMeasurement.Sha1.IsEmpty ||
            artifactMeasurement.Sha256 != artifact.FileSha256 || artifactMeasurement.Size != artifact.FileSize ||
            artifact.FileSha256 != measured.Measurement.ArtifactSha256 || artifact.FileSize != measured.Measurement.ArtifactSize ||
            string.IsNullOrWhiteSpace(encoderBuild) || encoderBuild.Length > 256 || encoderBuild.Any(char.IsControl) || comparedAt < measured.Measurement.ObservedAt)
            throw new ArgumentException("Actual encoding requires its exact independently measured artifact, encoder and comparison time.");
        Artifact = artifact;
        ArtifactMeasurement = artifactMeasurement;
        Measured = measured;
        Recipe = recipe;
        EncoderBuild = encoderBuild;
        ComparedAt = comparedAt;
    }
    public ITempFile Artifact { get; }
    public TempFileArtifactMeasurement ArtifactMeasurement { get; }
    public RomMeasuredRepresentation Measured { get; }
    public RecipeId Recipe { get; }
    public string EncoderBuild { get; }
    public DateTimeOffset ComparedAt { get; }
    public sealed class Whole : MediaReconciliationEncoded
    {
        public Whole(ITempFile artifact, RomMediaVerificationMeasurement.Whole sourceMeasurement, RomMediaVerificationMeasurement.Whole measurement, RecipeId recipe, string encoderBuild,
            RomChdEncoderBuild? nativeEncoder, DateTimeOffset comparedAt) : base(artifact, measurement.Artifact, measurement.Representation, recipe, encoderBuild, comparedAt)
        {
            ArgumentNullException.ThrowIfNull(sourceMeasurement);
            if (sourceMeasurement.Representation.Measurement.Outputs.Single().Identity != measurement.Representation.Measurement.Outputs.Single().Identity ||
                comparedAt < sourceMeasurement.Representation.Measurement.ObservedAt ||
                measurement.Representation.Manifest is not RomReconstructionManifest.WholeFile ||
                measurement.Representation.Container.Encoding is not (RomRepresentationEncoding.Rvz or RomRepresentationEncoding.Chd) ||
                (measurement.Representation.Container is RomContainerFacts.Chd) != (nativeEncoder is not null) ||
                nativeEncoder is not null && nativeEncoder.BuildLabel != encoderBuild)
                throw new ArgumentException("Whole encoding requires its measured whole-file codec and actual native encoder when applicable.");
            SourceMeasurement = sourceMeasurement;
            Measurement = measurement;
            NativeEncoder = nativeEncoder;
        }
        public RomMediaVerificationMeasurement.Whole SourceMeasurement { get; }
        public RomMediaVerificationMeasurement.Whole Measurement { get; }
        public RomChdEncoderBuild? NativeEncoder { get; }
    }
    public sealed class Cd : MediaReconciliationEncoded
    {
        public Cd(ITempFile artifact, TempFileArtifactMeasurement artifactMeasurement, RomMeasuredRepresentation measured, RomCdEncodingObservation observation, IReadOnlyDictionary<string, ITempFile> storedOriginals,
            DateTimeOffset comparedAt) : base(artifact, artifactMeasurement, measured, observation.Recipe, observation.Encoder.BuildLabel, comparedAt)
        {
            if (measured.Manifest is not RomReconstructionManifest.CdTracks cd || observation.MeasurementId != measured.Measurement.Id ||
                storedOriginals.Count != cd.Members.OfType<RomCdStoredMember>().Count() ||
                cd.Members.OfType<RomCdStoredMember>().Any(member => !storedOriginals.TryGetValue(member.Path, out var file) ||
                    file.FileSha256 != member.Identity.Sha256 || file.FileSize != member.Identity.Size))
                throw new ArgumentException("CD encoding requires every original stored descriptor and its actual source-request observation.");
            Observation = observation;
            StoredOriginals = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ITempFile>(new Dictionary<string, ITempFile>(storedOriginals, StringComparer.Ordinal));
        }
        public RomCdEncodingObservation Observation { get; }
        public IReadOnlyDictionary<string, ITempFile> StoredOriginals { get; }
    }
}
public interface IMediaReconciliationEncoding
{
    Task<ErrorOr<IMediaReconciliationEncodingLease>> AcquireAsync(MediaReconciliationAttempt attempt, CancellationToken ct = default);
}
public interface IMediaReconciliationEncodingLease : IAsyncDisposable
{
    bool ProcessTreeStopped { get; }
    Task<ErrorOr<MediaReconciliationEncoded>> EncodeAndVerifyAsync(Func<CancellationToken, Task> beforeVerification, CancellationToken ct = default);
    Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default);
}
