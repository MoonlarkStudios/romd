using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Storage;
using Romd.Storage.FileSystem;

namespace Romd.Infrastructure.Storage;

public sealed class RomContentReader : IRomContentReader
{
    private readonly IRomContentCatalog _catalog;
    private readonly IContentAddressableStore _cas;
    private readonly IRomRepresentationDecoder _decoder;
    private readonly IReadOnlyList<RomEncodingEntry> _entries;
    private readonly RomReadAdmissionDependencies _admission;

    public RomContentReader(IRomContentCatalog catalog, IContentAddressableStore cas, IRomRepresentationDecoder decoder,
        IRomChdDeliveryAdmission? chdAdmission = null)
        : this(catalog, cas, decoder, RomEncodingComposition.Entries, new RomReadAdmissionDependencies(chdAdmission)) { }

    /// <summary>Takes each encoding's read admission from its entry; the decoder alone knows which encodings it reads.</summary>
    internal RomContentReader(IRomContentCatalog catalog, IContentAddressableStore cas, IRomRepresentationDecoder decoder,
        IReadOnlyList<RomEncodingEntry> entries, RomReadAdmissionDependencies admission)
    {
        _catalog = catalog;
        _cas = cas;
        _decoder = decoder;
        _entries = entries;
        _admission = admission;
    }

    public async Task<Stream?> OpenAsync(Sha256 contentSha256, CancellationToken ct = default)
    {
        var content = await _catalog.FindAsync(contentSha256, ct);
        return content is null ? null : await OpenLogicalAsync(content, ct);
    }

    public async Task<Stream?> OpenAsync(RomDeliveryTarget target, CancellationToken ct = default,
        TimeSpan? admissionTimeout = null)
    {
        var stream = await OpenTargetAsync(target, ct, admissionTimeout);
        return stream is null ? null : new HashVerifyingStream(stream, StorageKey.FromHash(target.Sha256));
    }

    public Task<Stream?> OpenSeekableAsync(RomDeliveryTarget target, CancellationToken ct = default) =>
        OpenTargetAsync(target, ct, null);

    private async Task<Stream?> OpenTargetAsync(RomDeliveryTarget target, CancellationToken ct, TimeSpan? admissionTimeout)
    {
        if (!RomDeliveryTargets.IsValid(target))
            return null;
        Stream? stream;
        if (target.Format == RomDeliveryFormat.Original)
        {
            var current = await _catalog.FindByRomIdAsync(target.RomId, ct);
            if (current is null || current.ContentSha256 != target.ContentSha256 || current.ContentSize != target.ContentSizeBytes)
                return null;
            if (current.Reconstruction is RomReconstruction.CdMember member)
            {
                var decoder = _admission.ChdDelivery?.AssessCd(member);
                if (decoder is null || target.ReadBinding is not RomCdMemberBinding binding || binding != member.Bind(decoder))
                    return null;
            }
            else if (current.Encoding == RomRepresentationEncoding.Chd)
            {
                if (!current.Preferred.ChdEvidence.Any(evidence => evidence.AdmitsTarget(target, current, current.Preferred, _admission.ChdDelivery)))
                    return null;
            }
            else if (current.Encoding == RomRepresentationEncoding.Rvz)
            {
                if (target.ReadBinding is not RomManagedReadBinding binding || _admission.ChdDelivery?.AssessManaged(current.Preferred) != binding)
                    return null;
            }
            else if (target.ReadBinding is not null || target.ArtifactSha256 != current.ArtifactSha256 || target.FileId != current.FileId)
                return null;
            stream = await OpenLogicalAsync(current, ct, admissionTimeout);
        }
        else
        {
            if (EntryFor(target.Encoding).ReadAdmission(_admission).StoredTarget is { } recheck)
            {
                var current = await _catalog.FindByRomIdAsync(target.RomId, ct);
                if (current is null || !recheck(target, current))
                    return null;
            }
            var artifact = await _cas.OpenRandomAccessAsync(StorageKey.FromHash(target.ArtifactSha256), ct);
            if (artifact is null)
                return null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (artifact.Length != target.ArtifactSizeBytes)
                    throw new InvalidDataException("Artifact length disagrees with the bound delivery target.");
                stream = artifact.AsStream();
            }
            catch { await artifact.DisposeAsync(); throw; }
        }
        return stream;
    }

    public async Task<Stream?> OpenVerifiedByRomIdAsync(int romId, CancellationToken ct = default, TimeSpan? admissionTimeout = null)
    {
        var content = await _catalog.FindByRomIdAsync(romId, ct);
        if (content is null)
            return null;
        var stream = await OpenLogicalAsync(content, ct, admissionTimeout);
        return stream is null ? null : new HashVerifyingStream(stream, StorageKey.FromHash(content.ContentSha256));
    }

    private RomEncodingEntry EntryFor(RomRepresentationEncoding encoding) =>
        _entries.SingleOrDefault(entry => entry.Descriptor.Encoding == encoding) ??
        throw new NotSupportedException($"Unsupported ROM representation: {encoding}.");

    /// <summary>Project this exact already-bound descriptor; worker source pins may retain a non-preferred output.</summary>
    internal async Task<Stream?> OpenLogicalAsync(RomContentDescriptor content, CancellationToken ct, TimeSpan? admissionTimeout = null)
    {
        // Refusing an encoding the decoder cannot read leaves storage and that encoding's admission untouched.
        if (!_decoder.CanDecode(content.Encoding))
            return null;
        if (content.Reconstruction is RomReconstruction.CdMember member)
        {
            if (!member.Matches(content) || _admission.ChdDelivery?.AdmitsCd(member) != true ||
                member.Manifest.Members[member.Ordinal] is not RomCdBinMember)
                return null;
            var raw = _cas.OpenSynchronousRaw(StorageKey.FromHash(content.ArtifactSha256));
            if (raw is null)
                return null;
            var projected = await _decoder.DecodeAsync(RomRepresentationEncoding.Chd, RomArtifactSource.FromSynchronousRaw(raw),
                RomMediaOpenContext.ForCd(content.ArtifactSha256, member, admissionTimeout), ct);
            if (projected.Length != content.ContentSize)
            { await projected.DisposeAsync(); throw new InvalidDataException("CD member length disagrees with logical metadata."); }
            return projected.AsStream();
        }
        var entry = EntryFor(content.Encoding);
        var admitted = entry.ReadAdmission(_admission).Original(content);
        if (admitted is null)
            return null;
        // A raw-only encoding never opens compressed random access, even under a mis-wired admission.
        if (entry.Descriptor.RequiresRawStorage && admitted.Access != RomArtifactAccess.SynchronousRaw)
            return null;

        var decoded = admitted.Access == RomArtifactAccess.SynchronousRaw
            ? await DecodeSynchronousRawAsync(content, admitted, ct, admissionTimeout)
            : await DecodeRandomAccessAsync(content, ct, admissionTimeout);
        if (decoded is null)
            return null;
        if (decoded.Length != content.ContentSize)
        {
            await decoded.DisposeAsync();
            throw new InvalidDataException("Decoded length disagrees with logical ROM metadata.");
        }
        return decoded.AsStream();
    }

    private async Task<IRandomAccessContent?> DecodeRandomAccessAsync(RomContentDescriptor content, CancellationToken ct,
        TimeSpan? admissionTimeout)
    {
        var reader = await _cas.OpenRandomAccessAsync(StorageKey.FromHash(content.ArtifactSha256), ct);
        if (reader is null)
            return null;
        if (reader.Length != content.ArtifactSize)
        {
            await reader.DisposeAsync();
            throw new InvalidDataException("Artifact length disagrees with representation metadata.");
        }

        // Decoder owns the source even if construction fails.
        return await _decoder.DecodeAsync(content.Encoding, reader, ct, content.ArtifactSha256, admissionTimeout);
    }

    // A synchronous raw snapshot never converts, so this read cannot fall back to compressed random access.
    private async Task<IRandomAccessContent?> DecodeSynchronousRawAsync(RomContentDescriptor content,
        RomOriginalReadDecision admitted, CancellationToken ct, TimeSpan? admissionTimeout)
    {
        ct.ThrowIfCancellationRequested();
        var source = _cas.OpenSynchronousRaw(StorageKey.FromHash(content.ArtifactSha256));
        if (source is null)
            return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (source.Length != content.ArtifactSize)
                throw new InvalidDataException("CHD artifact length disagrees with retained evidence.");
        }
        catch { source.Dispose(); throw; }
        // The decoder owns this raw snapshot, including on refused admission or failed open.
        return await _decoder.DecodeAsync(content.Encoding, RomArtifactSource.FromSynchronousRaw(source),
            new RomMediaOpenContext(content.ArtifactSha256, admitted.ChdEvidence, admissionTimeout), ct);
    }
}
