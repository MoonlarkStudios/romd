using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Compressed;

/// <summary>Formats measured track-local geometry under exact catalog names. It establishes actual bytes, without claiming original preservation.</summary>
public interface IRomCdCueGenerator
{
    Task<ErrorOr<GeneratedCdCue>> GenerateAsync(IReadOnlyList<RomCdInspectedTrack> tracks,
        IReadOnlyList<string> trackNames, CancellationToken ct = default);
}

/// <summary>Owned generated descriptor custody and its complete actual identity. Expected catalog identity remains separate.</summary>
public sealed class GeneratedCdCue : IAsyncDisposable
{
    public GeneratedCdCue(ITempFile artifact, RomLogicalIdentity identity, string generatorProfile)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Size is < 1 or > 65536 || artifact.FileSize != identity.Size || artifact.FileSha256 != identity.Sha256 ||
            string.IsNullOrWhiteSpace(generatorProfile) || generatorProfile.Length > 120 || generatorProfile.Any(char.IsControl))
            throw new ArgumentException("A generated descriptor requires its bounded actual bytes and captured formatter profile.");
        Artifact = artifact;
        Identity = identity;
        GeneratorProfile = generatorProfile;
    }
    public ITempFile Artifact { get; }
    public RomLogicalIdentity Identity { get; }
    public string GeneratorProfile { get; }
    public ValueTask DisposeAsync() => Artifact.DisposeAsync();
}
