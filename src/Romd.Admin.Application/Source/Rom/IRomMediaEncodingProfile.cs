using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Stored representation parameters and output bounds, independent of a media kind's identity or eligibility.</summary>
public interface IRomMediaEncodingProfile
{
    RomRepresentationEncoding StoredEncoding { get; }
    string RecipeVersion { get; }
    RomRepresentationProfileClass Classify(RomContainerFacts container);
    /// <summary>Returns zero when the logical size cannot be encoded within the supplied limit.</summary>
    long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default);
}
