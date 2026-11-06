using System.Buffers.Binary;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Tests.Content;

/// <summary>Explicit pure fact matchers for application tests; Infrastructure pins the production registrations.</summary>
internal static class TestMediaKinds
{
    public static IReadOnlyList<MediaKindDescriptor> Descriptors { get; } =
    [
        Disc("gamecube-disc","gc",RomRepresentationEncoding.Rvz,"rvz-canonical/v1",["dolphin"],["rvz-import/v1","rvz-canonical/v1"],content=>Nintendo(content,1)),
        Disc("wii-disc","wii",RomRepresentationEncoding.Rvz,"rvz-canonical/v1",["dolphin"],["rvz-import/v1","rvz-canonical/v1"],content=>Nintendo(content,2)),
        Disc("ps2-dvd","ps2",RomRepresentationEncoding.Chd,"chd-dvd-canonical/v2",["pcsx2"],["chd-observed/v1"],DvdFacts),
        CdTracks("ps1-cd-set","psx","chd-ps1-canonical/v1",CdFacts) with { ConversionMode = ConversionMode.Automatic },
        new("opaque",[],RomRepresentationEncoding.Identity,null,MediaManifestType.WholeFile,ConversionMode.OnRequest,[],null,["identity/v1"])
    ];
    public static MediaKindCatalog Production { get; } = new(Descriptors);
    public static MediaKindDescriptor Disc(string kind, string platform, RomRepresentationEncoding encoding, string recipe,
        IReadOnlyList<string> runtimes, IReadOnlyList<string> profiles, Func<RomContentDescriptor, bool> matches) =>
        new(kind, [platform], encoding, recipe, MediaManifestType.WholeFile, ConversionMode.Automatic, runtimes, ".iso", profiles, matches);
    public static MediaKindDescriptor CdTracks(string kind, string platform, string recipe, Func<RomContentDescriptor, bool> matches) =>
        new(kind, [platform], RomRepresentationEncoding.Chd, recipe, MediaManifestType.CdTracks, ConversionMode.OnRequest, [], null, ["chd-cd-whole-files/v1", "chd-cd-retained-files/v2"], matches);
    public static MediaKindCatalog WithRuntimes(Func<MediaKindDescriptor, IReadOnlyList<string>> runtimes) =>
        new(Descriptors.Select(kind => kind.StoredEncoding == RomRepresentationEncoding.Identity ? kind : kind with { NativeRuntimes = runtimes(kind) }));
    public static bool CdFacts(RomContentDescriptor content) => content.Artifacts.Any(artifact => artifact.HasMeasuredEvidence && artifact.Facts.Manifest is RomReconstructionManifest.CdTracks &&
        artifact.Output.Selector is RomOutputSelector.Member && artifact.Container is RomContainerFacts.Chd { UnitBytes: 2448 } chd && chd.Metadata.All(entry => entry.Tag == 0x43485432 && entry.Flags == 1));
    public static bool DvdFacts(RomContentDescriptor content) => content.Artifacts.Any(artifact => artifact.HasMeasuredEvidence &&
        artifact.Facts.Manifest is RomReconstructionManifest.WholeFile && artifact.Container is RomContainerFacts.Chd { UnitBytes: 2048 } chd &&
        chd.Codecs.All(codec => codec is RomChdCodecs.None or RomChdCodecs.Zlib or RomChdCodecs.Lzma or RomChdCodecs.Huffman or RomChdCodecs.Flac or RomChdCodecs.Zstd) &&
        chd.Metadata is [var metadata] && metadata.Tag == 0x44564420 && metadata.Flags == 1 && metadata.Payload.SequenceEqual(new byte[] { 0 })) ||
        content.Inspections.Any(inspection => inspection.Layout is RomPs2BootLayoutFacts) && content.References.Count is > 0 and <= 128 &&
        content.References.All(reference => reference.System == RomDiscReferenceSystem.PlayStation2 && reference.Media is RomDiscReferenceMedia.Dvd5 or RomDiscReferenceMedia.Dvd9 && reference.FileCount == 1) &&
        content.References.Select(reference => reference.Media).Distinct().Count() == 1;
    private static bool Nintendo(RomContentDescriptor content, uint discType)
    {
        bool Matches(RomLogicalInspection inspection) => inspection.Prefix.Length == 32 && inspection.Layout is RomNintendoLayoutFacts layout &&
            BinaryPrimitives.ReadUInt32BigEndian(inspection.Prefix[(discType == 1 ? 0x1c : 0x18)..]) == (discType == 1 ? 0xc2339f3dU : 0x5d1c9ea3U) &&
            (discType == 1 ? layout.Partitions == 0 : layout.Partitions > 0);
        var encoded = content.Artifacts.Where(artifact => artifact.HasMeasuredEvidence && artifact.Container is RomContainerFacts.Rvz)
            .Select(artifact => (RomContainerFacts.Rvz)artifact.Container).ToArray();
        return encoded.Length > 0 ? encoded.All(rvz => rvz.DiscType == discType) && content.Inspections.Where(inspection => inspection.Layout is RomNintendoLayoutFacts).All(Matches) :
            content.Inspections.Any(Matches);
    }
}
