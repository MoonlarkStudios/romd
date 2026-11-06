using Romd.Infrastructure.Storage;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Inspection;
using Romd.Media.Rvz;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Ephemeral read fixtures measured from actual CAS bytes before HTTP timing begins.</summary>
internal sealed record MeasuredProbeContent(RomContentDescriptor Logical, RomContentDescriptor Opaque)
{
    internal static async Task<MeasuredProbeContent> VerifyRvzAsync(IContentAddressableStore cas, StorageKey key, Digests expected, CancellationToken ct)
    {
        Digests raw;
        await using (var source = await cas.OpenRandomAccessAsync(key, ct) ?? throw new InvalidDataException("Probe artifact is missing."))
        await using (var stream = source.AsStream(leaveOpen: true))
            raw = await Digests.StreamAsync(stream, ct);
        Check.That(raw.Sha256 == key.Hash.ToString(), "probe measurement hashes exact CAS artifact bytes");
        await using var image = await RvzImage.OpenAsync(await cas.OpenRandomAccessAsync(key, ct) ?? throw new InvalidDataException("Probe artifact is missing."),
            new RvzReadOptions(), new MediaBlockCache(new ReadDecoderOptions().CacheBytes), ct: ct);
        Digests decoded;
        await using (var stream = image.AsStream(leaveOpen: true))
            decoded = await Digests.StreamAsync(stream, ct);
        Check.That(decoded == expected, "complete probe output equals independently measured original hashes");
        var header = image.Descriptor;
        var container = new RomContainerFacts.Rvz(header.Version, header.DiscType, header.Compression, header.CompressionLevel,
            header.ChunkSize, header.HasWiiPartitions, image.Length);
        return new(Describe("disc.iso", raw, decoded, container, MediaInspectionService.DecoderBuild),
            Describe("disc.rvz", raw, raw, RomContainerFacts.Identity.Instance, "probe-raw-hasher/v1"));
    }
    private static RomContentDescriptor Describe(string filename, Digests raw, Digests decoded, RomContainerFacts container, string decoder)
    {
        var identity = new RomLogicalIdentity(decoded.Size, Sha256.Parse(decoded.Sha256), Sha1.Parse(decoded.Sha1), Md5.Parse(decoded.Md5), Crc32.Parse(decoded.Crc));
        var proof = new RomRepresentationVerification(Guid.NewGuid(), Sha256.Parse(raw.Sha256), raw.Size, new RomDecoderIdentity.Managed(decoder),
            "probe-complete-read/v1", "probe/mvid:" + typeof(MeasuredProbeContent).Assembly.ManifestModule.ModuleVersionId.ToString("D"), DateTimeOffset.UtcNow,
            [new(RomOutputSelector.Whole.Instance, identity)], container);
        var facts = new RomRepresentationFacts(1, 1, proof.ArtifactSha256, proof.ArtifactSize, container, RomReconstructionManifest.WholeFile.Instance,
            [new(1, 1, RomOutputSelector.Whole.Instance, identity)], [proof], []);
        return new(1, 1, identity, filename, [new(facts, 1, true, Md5.Parse(raw.Md5), Sha1.Parse(raw.Sha1))], [], []);
    }
}
