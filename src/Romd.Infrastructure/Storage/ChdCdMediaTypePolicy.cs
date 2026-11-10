using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Pure matching of the CD adapter's measured reconstruction and raw container facts.</summary>
internal static class ChdCdMediaTypePolicy
{
    private const uint _frameBytes = 2448;
    private const uint _trackMetadataTag = 0x43485432;
    private const byte _checksummedMetadata = 1;

    public static bool MatchesGraph(RomRepresentationFacts graph) => graph.Verifications.Count > 0 &&
        graph.Manifest is RomReconstructionManifest.CdTracks &&
        graph.Container is RomContainerFacts.Chd { UnitBytes: _frameBytes } chd &&
        chd.Metadata.All(entry => entry.Tag == _trackMetadataTag && entry.Flags == _checksummedMetadata);

    public static bool MatchesBoot(RomRepresentationFacts graph, string profile) => MatchesGraph(graph) &&
        graph.CdBootInspections.All(observation => observation.Boot.Profile == profile);

    public static bool MatchesFacts(RomContentDescriptor content, string profile) => content.Artifacts.Any(artifact =>
        artifact.Output.Selector is RomOutputSelector.Member && MatchesBoot(artifact.Facts, profile));

    public static bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return content.Artifacts.Any(artifact => artifact.Output.Selector is RomOutputSelector.Member && MatchesGraph(artifact.Facts));
    }
}
