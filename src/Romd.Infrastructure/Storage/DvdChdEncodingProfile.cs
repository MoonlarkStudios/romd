using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Media.Chd;

namespace Romd.Infrastructure.Storage;

/// <summary>The shared DVD CHD recipe and physical output bound, independent of the disc's platform.</summary>
public sealed class DvdChdEncodingProfile : IRomMediaEncodingProfile
{
    private readonly long _maximumLogicalBytes;

    public DvdChdEncodingProfile(long maximumLogicalBytes = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLogicalBytes);
        _maximumLogicalBytes = maximumLogicalBytes;
    }

    public RomRepresentationEncoding StoredEncoding => RomRepresentationEncoding.Chd;
    public string RecipeVersion => ChdDvdCanonicalRecipe.Id;
    public RomRepresentationProfileClass Classify(RomContainerFacts container) => container is not RomContainerFacts.Chd chd || chd.UnitBytes != ChdEncodingRecipe.UnitBytes
        ? RomRepresentationProfileClass.Unknown
        : MatchesCanonical(chd) ? RomRepresentationProfileClass.Canonical : RomRepresentationProfileClass.NonCanonical;
    public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) =>
        ChdDvdOutputBounds.GetMaximumOutputBytes(logicalBytes, Math.Min(maxLogicalBytes, _maximumLogicalBytes), ct);

    private static bool MatchesCanonical(RomContainerFacts.Chd parameters) =>
        parameters is { Version: 5, UnitBytes: ChdEncodingRecipe.UnitBytes, HunkBytes: ChdEncodingRecipe.HunkBytes, HasParent: false } &&
        parameters.Codecs.SequenceEqual(new uint[] { ChdEncodingRecipe.Codec0, 0, 0, 0 }) &&
        parameters.Metadata is [var metadata] && metadata.Tag == 0x44564420 && metadata.Flags == 1 &&
        metadata.Payload.SequenceEqual(new byte[] { 0 });
}
