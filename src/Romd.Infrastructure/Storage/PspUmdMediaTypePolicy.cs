using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Whole-file PSP UMD storage; recognition comes from independently measured logical filesystem facts.</summary>
public sealed class PspUmdMediaTypePolicy : IRomMediaTypePolicy
{
    public PspUmdMediaTypePolicy(string platformKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformKey);
        PlatformKey = platformKey;
    }
    public RomIsoDiscKind DiscKind => RomIsoDiscKind.PlayStationPortableUmd;
    public string PlatformKey { get; }
    public IRomMediaEncodingProfile EncodingProfile { get; } = new DvdChdEncodingProfile(PspUmdIdentifier.MaximumLogicalBytes);
    public bool MatchesFacts(RomContentDescriptor content) =>
        content.Observes<RomPspLayoutFacts>() && content.ObservesNoLayoutOtherThan<RomPspLayoutFacts>();
    public bool ObservesEncodingLayout(RomLogicalInspection inspection) => inspection.Layout is RomPspLayoutFacts;
    public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return content.Identity.Size <= PspUmdIdentifier.MaximumLogicalBytes && MatchesFacts(content);
    }
}
