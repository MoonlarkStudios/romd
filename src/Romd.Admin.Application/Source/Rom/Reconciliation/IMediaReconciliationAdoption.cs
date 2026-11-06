using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>A fresh comparison of authentic originals against retained bytes. Its lease owns all staged files.</summary>
public sealed class MediaReconciliationAdopted
{
    public MediaReconciliationAdopted(ITempFile artifact, RomMediaVerificationMeasurement.Cd measurement,
        IReadOnlyDictionary<string, ITempFile> storedOriginals)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.Representation.Manifest is not RomReconstructionManifest.CdTracks cd ||
            measurement.Representation.Measurement.Decoder is not RomDecoderIdentity.Native ||
            artifact.FileSha256 != measurement.Representation.Measurement.ArtifactSha256 || artifact.FileSize != measurement.Representation.Measurement.ArtifactSize ||
            measurement.Artifact.Sha256 != artifact.FileSha256 || measurement.Artifact.Size != artifact.FileSize ||
            string.IsNullOrWhiteSpace(measurement.InspectorBuild) ||
            storedOriginals.Count != cd.Members.OfType<RomCdStoredMember>().Count() ||
            cd.Members.OfType<RomCdStoredMember>().Any(member => !storedOriginals.TryGetValue(member.Path, out var file) ||
                file.FileSha256 != member.Identity.Sha256 || file.FileSize != member.Identity.Size))
            throw new ArgumentException("Adoption requires the exact independently compared archive and every authentic stored original.");
        Artifact = artifact;
        Measurement = measurement;
        StoredOriginals = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ITempFile>(new Dictionary<string, ITempFile>(storedOriginals, StringComparer.Ordinal));
    }
    public ITempFile Artifact { get; }
    public RomMediaVerificationMeasurement.Cd Measurement { get; }
    public RomMeasuredRepresentation Measured => Measurement.Representation;
    public IReadOnlyDictionary<string, ITempFile> StoredOriginals { get; }
}
public interface IMediaReconciliationAdoption
{
    Task<ErrorOr<IMediaReconciliationAdoptionLease>> AcquireAsync(MediaReconciliationAttempt attempt, CancellationToken ct = default);
}
public interface IMediaReconciliationAdoptionLease : IAsyncDisposable
{
    bool ProcessTreeStopped { get; }
    Task<ErrorOr<MediaReconciliationAdopted>> CompareOriginalsAsync(CancellationToken ct = default);
    Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default);
}
