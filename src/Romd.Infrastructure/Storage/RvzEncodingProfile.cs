using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Media.Rvz;

namespace Romd.Infrastructure.Storage;

/// <summary>The shared RVZ recipe with the output bound of its registered disc family.</summary>
public sealed class RvzEncodingProfile : IRomMediaEncodingProfile
{
    private readonly RvzDiscFamily _family;

    public RvzEncodingProfile(RvzDiscFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        _family = family;
    }

    public RomRepresentationEncoding StoredEncoding => RomRepresentationEncoding.Rvz;
    public string RecipeVersion => RvzCanonicalRecipe.Id;
    public RomRepresentationProfileClass Classify(RomContainerFacts container) => container is not RomContainerFacts.Rvz rvz
        ? RomRepresentationProfileClass.Unknown
        : RvzCanonicalRecipe.Matches(rvz.Compression, rvz.CompressionLevel, rvz.BlockBytes)
            ? RomRepresentationProfileClass.Canonical : RomRepresentationProfileClass.NonCanonical;
    public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) =>
        _family.GetOutputBound(logicalBytes, maxLogicalBytes, ct) is RvzCanonicalOutputBound.Success bound ? bound.MaximumOutputBytes : 0;
}
