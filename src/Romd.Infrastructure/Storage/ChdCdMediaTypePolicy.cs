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

    public static bool MatchesFacts(RomContentDescriptor content) => content.Artifacts.Any(artifact =>
        artifact.Output.Selector is RomOutputSelector.Member && MatchesGraph(artifact.Facts));

    public static bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return MatchesFacts(content);
    }
}
