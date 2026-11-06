using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>The managed RVZ recipe and its evidence rules for one Nintendo disc family, on the platform its kind registers.</summary>
public sealed class RvzMediaTypePolicy : IRomMediaTypePolicy
{
    private readonly RvzDiscFamily _family;

    public RvzMediaTypePolicy(RvzDiscFamily family, string platformKey)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformKey);
        _family = family;
        EncodingProfile = new RvzEncodingProfile(family);
        PlatformKey = platformKey;
    }
    public RomIsoDiscKind DiscKind => _family.DiscKind;
    public string PlatformKey { get; }
    public IRomMediaEncodingProfile EncodingProfile { get; }

    public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return MatchesFacts(content);
    }

    public bool MatchesFacts(RomContentDescriptor content)
    {
        var encoded = content.Artifacts.Where(artifact => artifact.HasMeasuredEvidence && artifact.Container is RomContainerFacts.Rvz)
            .Select(artifact => (RomContainerFacts.Rvz)artifact.Container).ToArray();
        // A verified RVZ declares its disc family independently of whether this encoder supports the logical layout.
        // Raw originals need structural inspection; known contradictory observations cannot be overridden by a header.
        return content.ObservesNoLayoutOtherThan<RomNintendoLayoutFacts>() && (encoded.Length > 0
            ? encoded.All(rvz => rvz.DiscType == _family.ContainerDiscType) && content.Inspections.Where(ObservesEncodingLayout).All(MatchesObservation)
            : content.Inspections.Any(MatchesObservation));
    }
    public bool ObservesEncodingLayout(RomLogicalInspection inspection) => inspection.Layout is RomNintendoLayoutFacts;
    private bool MatchesObservation(RomLogicalInspection inspection) => inspection.Prefix.Length == 32 &&
        inspection.Layout is RomNintendoLayoutFacts layout && _family.MatchesHeader(inspection.Prefix) && _family.MatchesLayout(layout);
}
