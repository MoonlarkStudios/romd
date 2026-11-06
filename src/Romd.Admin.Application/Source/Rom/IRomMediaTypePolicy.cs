using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Recognition and conversion eligibility for one inspected media kind.</summary>
public interface IRomMediaTypePolicy
{
    RomIsoDiscKind DiscKind { get; }
    string PlatformKey { get; }
    IRomMediaEncodingProfile EncodingProfile { get; }
    bool MatchesFacts(RomContentDescriptor content);
    /// <summary>Whether these current logical observations authorize a new encoding of this kind.</summary>
    bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct);
    /// <summary>Whether one fresh source inspection carries the layout family this kind encodes.</summary>
    bool ObservesEncodingLayout(RomLogicalInspection inspection);
}
