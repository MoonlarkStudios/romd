using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Application.Common.Content;

namespace Romd.Application.Tests.Source.Rom;

internal static class TestRomMediaPolicies
{
    internal static RomMediaPolicyRegistry Create() => new([new Policy()]);
    private sealed class Policy : IRomMediaTypePolicy, IRomMediaEncodingProfile
    {
        public IRomMediaEncodingProfile EncodingProfile => this;
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => false;
        public RomIsoDiscKind DiscKind => RomIsoDiscKind.GameCube;
        public string PlatformKey => "gc";
        public RomRepresentationEncoding StoredEncoding => RomRepresentationEncoding.Rvz;
        public string RecipeVersion => "recipe/v1";
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => container is RomContainerFacts.Rvz rvz
            ? rvz.CompressionLevel switch
            {
                19 => RomRepresentationProfileClass.Canonical,
                12 => RomRepresentationProfileClass.NonCanonical,
                _ => RomRepresentationProfileClass.Unknown
            } : RomRepresentationProfileClass.Unknown;
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct) => throw new InvalidOperationException("This fixture does not authorize encoding.");
        public bool MatchesFacts(RomContentDescriptor content) => content.Artifacts.Any(artifact =>
            artifact.HasMeasuredEvidence && artifact.Container is RomContainerFacts.Rvz { DiscType: 1 });
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) => logicalBytes;
    }
}
