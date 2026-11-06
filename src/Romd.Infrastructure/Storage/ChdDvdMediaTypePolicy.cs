using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Private recipe candidate for an independently established PlayStation 2 DVD disc kind.</summary>
/// <remarks>Policy registration/activation requires target qualification; this policy cannot establish DVD eligibility.</remarks>
public sealed class ChdDvdMediaTypePolicy : IRomMediaTypePolicy
{
    public ChdDvdMediaTypePolicy(string platformKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformKey);
        PlatformKey = platformKey;
    }
    public RomIsoDiscKind DiscKind => RomIsoDiscKind.PlayStation2Dvd;
    public string PlatformKey { get; }
    public IRomMediaEncodingProfile EncodingProfile { get; } = new DvdChdEncodingProfile();
    public bool MatchesFacts(RomContentDescriptor content) =>
        content.ObservesNoLayoutOtherThan<RomPs2BootLayoutFacts>() && (
        content.Artifacts.Any(artifact => artifact.HasMeasuredEvidence && artifact.Facts.Manifest is RomReconstructionManifest.WholeFile &&
            artifact.Container is RomContainerFacts.Chd { UnitBytes: 2048, HasParent: false } chd && chd.LogicalBytes % 2048 == 0 &&
            chd.Codecs.All(codec => codec is RomChdCodecs.None or RomChdCodecs.Zlib or RomChdCodecs.Lzma or RomChdCodecs.Huffman or RomChdCodecs.Flac or RomChdCodecs.Zstd) &&
            chd.Metadata is [var metadata] && metadata.Tag == 0x44564420 && metadata.Flags == 1 && metadata.Payload.SequenceEqual(new byte[] { 0 })) ||
        content.Observes<RomPs2BootLayoutFacts>() && content.References.Count is > 0 and <= 128 &&
        content.References.All(reference => reference.System == RomDiscReferenceSystem.PlayStation2 &&
            reference.Media is RomDiscReferenceMedia.Dvd5 or RomDiscReferenceMedia.Dvd9 && reference.FileCount == 1) &&
        content.References.Select(reference => reference.Media).Distinct().Count() == 1);
    public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
    {
        var boot = content.Inspections.Where(ObservesEncodingLayout)
            .OrderByDescending(row => row.ObservedAt).ThenBy(row => row.Id).FirstOrDefault();
        var authority = RomPs2DvdEligibilityResolver.ResolveFacts(content.Identity, boot?.Layout as RomPs2BootLayoutFacts,
            boot?.ObservedAt ?? DateTimeOffset.UnixEpoch, content.References, ct);
        return !authority.IsError && authority.Value.Status == RomPs2DvdResolutionStatus.Supported;
    }
    public bool ObservesEncodingLayout(RomLogicalInspection inspection) => inspection.Layout is RomPs2BootLayoutFacts;

}
